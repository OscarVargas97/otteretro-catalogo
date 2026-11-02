using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;
using Dapper;

namespace Otteretro.Claude;

/// <summary>Tokens de una llamada, como los informa `claude` (para el registro de uso, F2.3).</summary>
public record ClaudeUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens);

public record ClaudeResult<T>(T Value, ClaudeUsage Usage, TimeSpan Duration, int Attempts);

/// <summary>Lo que devuelve un proceso.</summary>
public record ProcessOutput(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Ejecuta `claude -p` con el Claude Code del usuario, nunca la API (P5, F2.1). La respuesta es JSON con la forma de
/// <typeparamref name="T"/>: el esquema sale de ese tipo, `claude` lo exige con `--json-schema` y acá se vuelve a validar
/// al leerlo. Si no valida, se pregunta una vez más. Solo tiene búsqueda y lectura web: no ejecuta nada ni toca archivos.
/// </summary>
public static class ClaudeRunner
{
    /// <summary>Lanza el proceso. Las pruebas ponen aquí un `claude` falso.</summary>
    public static Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> Launch { get; set; } = RunProcess;

    public static string Command { get; set; } = "claude";

    public const string Tools = "WebSearch,WebFetch";

    /// <summary>Lectura estricta: faltan campos obligatorios, sobran campos o hay un null donde no va → no valida.</summary>
    static readonly JsonSerializerOptions Strict = new(JsonSerializerOptions.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>La salida de `claude` viene en snake_case.</summary>
    static readonly JsonSerializerOptions Snake = new(JsonSerializerOptions.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>El prompt `prompts/&lt;name&gt;.md` del repo, que va dentro del ejecutable.</summary>
    public static string Prompt(string name)
    {
        using var stream = typeof(ClaudeRunner).Assembly.GetManifestResourceStream($"prompts/{name}.md")
            ?? throw new InvalidOperationException($"No existe el prompt {name}");
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>El esquema JSON de <typeparamref name="T"/> que recibe `--json-schema`.</summary>
    public static string Schema<T>() => JsonSchemaExporter.GetJsonSchemaAsNode(Strict, typeof(T),
        new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }).ToJsonString(); // la raíz va como "object", no "object o null"

    /// <param name="db">Donde queda el registro de uso: una fila de `claude_usage` por intento (F2.3).</param>
    /// <param name="taskId">La tarea que llama, para el registro.</param>
    /// <param name="name">El nombre del prompt, para el registro.</param>
    /// <param name="prompt">Las instrucciones (normalmente <see cref="Prompt"/>).</param>
    /// <param name="input">Los datos, que van por la entrada estándar como JSON.</param>
    /// <param name="model">haiku, sonnet u opus; null usa el del Claude Code del usuario.</param>
    public static async Task<ClaudeResult<T>> Run<T>(Database db, long? taskId, string name, string prompt, object input, CancellationToken ct, string? model = null)
    {
        var start = new ProcessStartInfo(Command)
        {
            // Una carpeta vacía: que no lea el CLAUDE.md ni la configuración de ningún proyecto.
            WorkingDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "otteretro-claude")).FullName,
            // La app es WinExe: sin esto Windows abre una terminal por cada llamada.
            CreateNoWindow = true,
        };
        foreach (var arg in (string[])["-p", prompt, "--output-format", "json", "--json-schema", Schema<T>(),
                     "--restricted", "--tools", Tools, "--strict-mcp-config", "--no-session-persistence"])
            start.ArgumentList.Add(arg);
        if (model is not null)
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(model);
        }
        var stdin = JsonSerializer.Serialize(input, JsonSerializerOptions.Web);

        var clock = Stopwatch.StartNew();
        var usage = new ClaudeUsage(0, 0, 0, 0);
        for (var attempt = 1; ; attempt++)
        {
            ClaudeBudget.Check(db);
            ProcessOutput output;
            var call = Stopwatch.StartNew();
            try { output = await Launch(start, stdin, ct); }
            catch (Win32Exception) { throw new TaskException(ApiError.Of("tasks.claude.notInstalled")); }

            var (value, reply, error) = Read<T>(output);
            if (reply?.Usage is { } used) usage = Add(usage, used);
            var limit = ClaudeBudget.LimitReached(db, $"{reply?.Result}\n{output.Stderr}");
            Log(db, taskId, name, model, call.Elapsed, limit is not null ? "limit" : error?.Code switch
            {
                null => "ok",
                "tasks.claude.invalidOutput" => "invalid",
                _ => "failed",
            }, reply?.Usage ?? new ClaudeUsage(0, 0, 0, 0), reply?.TotalCostUsd ?? 0);
            if (limit is not null) throw limit;
            if (error is null) return new ClaudeResult<T>(value!, usage, clock.Elapsed, attempt);
            FileLog.Write(LogLevel.Warning, "Claude", $"Intento {attempt}: {error.Code} {output.Stderr}");
            // Solo se repite lo que respondió mal; si `claude` falló, preguntar otra vez no lo arregla.
            if (error.Code != "tasks.claude.invalidOutput" || attempt == 2) throw new TaskException(error);
        }
    }

    static (T? Value, Reply? Reply, ApiError? Error) Read<T>(ProcessOutput output)
    {
        Reply? reply;
        try { reply = JsonSerializer.Deserialize<Reply>(output.Stdout, Snake); }
        catch (JsonException) { reply = null; }
        if (reply is null || output.ExitCode != 0 || reply.IsError)
        {
            var detail = reply?.Result ?? (output.Stderr.Trim() is { Length: > 0 } err ? err : $"exit {output.ExitCode}");
            return (default, reply, ApiError.Of("tasks.claude.failed", ("detail", detail)));
        }
        try
        {
            if (reply.StructuredOutput is { } node && node.Deserialize<T>(Strict) is { } value) return (value, reply, null);
        }
        catch (JsonException) { }
        return (default, reply, ApiError.Of("tasks.claude.invalidOutput"));
    }

    static void Log(Database db, long? taskId, string prompt, string? model, TimeSpan duration, string result, ClaudeUsage u, double cost)
    {
        App.TaskQueue.Log(db, taskId, ApiError.Of("tasks.log.claude", ("prompt", prompt), ("model", model ?? "—"), ("result", result),
            ("input", (u.InputTokens + u.CacheReadInputTokens + u.CacheCreationInputTokens).ToString()), ("output", u.OutputTokens.ToString()),
            ("seconds", ((int)duration.TotalSeconds).ToString())), result == "ok" ? "info" : "warn");
        using var c = db.Open();
        c.Execute("""
            INSERT INTO claude_usage (task_id, prompt, model, duration_ms, result, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, cost_usd, created_at)
            VALUES (@taskId, @prompt, @model, @ms, @result, @InputTokens, @OutputTokens, @CacheReadInputTokens, @CacheCreationInputTokens, @cost, @now)
            """, new
        {
            taskId,
            prompt,
            model,
            ms = (long)duration.TotalMilliseconds,
            result,
            u.InputTokens,
            u.OutputTokens,
            u.CacheReadInputTokens,
            u.CacheCreationInputTokens,
            cost,
            now = ClaudeBudget.UtcNow().ToString("O")
        });
    }

    static ClaudeUsage Add(ClaudeUsage a, ClaudeUsage b) => new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens,
        a.CacheReadInputTokens + b.CacheReadInputTokens, a.CacheCreationInputTokens + b.CacheCreationInputTokens);

    /// <summary>Lo que se usa de la salida de `claude -p --output-format json`.</summary>
    record Reply(bool IsError, string? Result, JsonNode? StructuredOutput, ClaudeUsage? Usage, double TotalCostUsd);

    static async Task<ProcessOutput> RunProcess(ProcessStartInfo start, string stdin, CancellationToken ct)
    {
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return new ProcessOutput(process.ExitCode, await stdout, await stderr);
    }
}

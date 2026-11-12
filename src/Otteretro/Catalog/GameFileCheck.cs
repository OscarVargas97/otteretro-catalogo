using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// La tarea «Archivo del juego» (#198, de #182): cómo recibe cada port el archivo del juego (<c>gameFile</c> de su receta), para que
/// Otteretro solo siga la instrucción. Se puede volver a correr cuando entran ports: toma los que no tienen <c>gameFile</c> y no se
/// vieron con su <c>asset</c> actual (<c>gamefile_seen</c>).
/// <para>Para gastar poco, la evidencia la junta un script por la API de GitHub (las partes del README que hablan del juego y los
/// nombres de archivos del repo) y Haiku solo decide con eso, de a 5 ports por llamada. Lo seguro se guarda sin publicar; lo dudoso
/// queda como aviso en la tarjeta de la receta; lo que no se sabe queda visto hasta que cambie el <c>asset</c>.</para>
/// </summary>
public static partial class GameFileCheck
{
    public const string Kind = "ports.gamefile", PromptName = "archivo-del-juego", Model = "haiku";

    /// <summary>Los ports que van a Claude en una ejecución; el resto queda para la próxima.</summary>
    public static int MaxCandidates { get; set; } = 60;
    const int Batch = 5, ReadmeChars = 5000, MaxPaths = 60;

    /// <param name="Deliver">arg, copy, none, manual o unknown (no se sabe con lo que hay).</param>
    /// <param name="Sure">Si la evidencia lo dice claro; si no, va como aviso.</param>
    public record Answer(string Recipe, string Deliver, List<string> Args, string CopyTo, List<string> Formats, bool Sure, string Reason);
    public record Reply(List<Answer> Ports);

    record Port(string Id, string Repo, string Asset, JsonObject Json);

    /// <summary>Los ports de GitHub sin <c>gameFile</c> que no se vieron con su <c>asset</c> actual: lo que encola y su contador.</summary>
    const string Missing = """
        FROM recipe r LEFT JOIN gamefile_seen s ON s.recipe = r.id AND s.asset = COALESCE(json_extract(r.json, '$.asset'), '')
        WHERE r.kind = 'port' AND json_extract(r.json, '$.gameFile') IS NULL AND json_extract(r.json, '$.updates.source') = 'github'
          AND json_extract(r.json, '$.updates.repo') IS NOT NULL AND s.recipe IS NULL
        """;

    public static long Enqueue(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = @Kind AND status IN ('queued', 'running') LIMIT 1", new { Kind }) ?? TaskQueue.Enqueue(db, Kind);
    }

    public static int Pending(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<int>("SELECT count(*) " + Missing);
    }

    public static async Task Run(TaskRun run, CancellationToken ct)
    {
        var db = run.Db;
        var token = run.Credentials.Read("github.token") is { Length: > 0 } t ? t : null;
        List<Port> ports;
        using (var c = db.Open())
            ports = c.Query<(string Id, string Json)>("SELECT r.id, r.json " + Missing + " ORDER BY r.id LIMIT @MaxCandidates", new { MaxCandidates })
                .Select(x => JsonNode.Parse(x.Json)!.AsObject())
                .Select(j => new Port((string)j["id"]!, (string)j["updates"]!["repo"]!, (string?)j["asset"] ?? "", j))
                .ToList();

        var prompt = ClaudeRunner.Prompt(PromptName);
        var (set, doubtful, unknown) = (0, 0, 0);
        for (var i = 0; i < ports.Count; i += Batch)
        {
            var batch = ports.Skip(i).Take(Batch).ToList();
            run.Report(i * 100 / ports.Count, ApiError.Of("gamefile.reading", ("done", i.ToString()), ("total", ports.Count.ToString())));
            var input = new List<object>();
            foreach (var p in batch)
            {
                try { input.Add(await Evidence(p, token, ct)); }
                catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    run.Log(ApiError.Of("gamefile.log.rateLimited"), "warn"); // lo que falta, la próxima vez
                    i = ports.Count;
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or JsonException or FormatException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    run.Log(ApiError.Of("gamefile.log.failed", ("recipe", p.Id), ("detail", e.Message)), "warn");
                }
            }
            if (input.Count == 0) continue;

            var reply = await ClaudeRunner.Run<Reply>(db, run.Id, PromptName, prompt, new { ports = input }, ct, Model);
            foreach (var p in batch)
            {
                // Lo que Claude no respondió se vuelve a pedir la próxima vez.
                if (reply.Value.Ports.FirstOrDefault(a => a.Recipe == p.Id) is not { } a) continue;
                if (a.Deliver == "unknown" || ToGameFile(a) is not { } gameFile)
                {
                    Seen(db, p, "unknown", a.Reason);
                    run.Log(ApiError.Of("gamefile.log.unknown", ("recipe", p.Id), ("reason", a.Reason)));
                    unknown++;
                }
                else if (a.Sure && Store(db, p, gameFile))
                {
                    Seen(db, p, "set", a.Reason);
                    run.Log(ApiError.Of("gamefile.log.set", ("recipe", p.Id), ("gameFile", gameFile.ToJsonString())));
                    set++;
                }
                else
                {
                    Notice(db, p, gameFile, a.Reason);
                    Seen(db, p, "doubtful", a.Reason);
                    run.Log(ApiError.Of("gamefile.log.doubtful", ("recipe", p.Id), ("gameFile", gameFile.ToJsonString())), "warn");
                    doubtful++;
                }
            }
        }
        run.Log(ApiError.Of("gamefile.log.done", ("set", set.ToString()), ("doubtful", doubtful.ToString()), ("unknown", unknown.ToString()),
            ("left", Pending(db).ToString())));
    }

    /// <summary>
    /// Lo que ve Claude de un port, sin gastar en leer: su receta, las partes del README que hablan del juego (ROM, dump, extraer…) y
    /// los nombres de archivos del repo que pueden decir cómo lo busca.
    /// </summary>
    static async Task<object> Evidence(Port p, string? token, CancellationToken ct)
    {
        var api = "https://api.github.com/repos/" + p.Repo;
        var readme = await Recipes.Api(api + "/readme", token, ct) is { } r && (string?)r["content"] is { } b64
            ? Excerpt(Encoding.UTF8.GetString(Convert.FromBase64String(b64.Replace("\n", ""))))
            : "";
        var paths = await Recipes.Api(api + "/git/trees/HEAD?recursive=1", token, ct) is { } tree && tree["tree"] is JsonArray items
            ? items.Select(x => (string?)x?["path"]).OfType<string>().Where(x => UsefulPath().IsMatch(x)).Take(MaxPaths).ToList()
            : [];
        return new
        {
            recipe = p.Id,
            name = (string?)p.Json["name"] ?? p.Id,
            repo = "https://github.com/" + p.Repo,
            consoles = p.Json["consoles"],
            detect = (string?)p.Json["detect"] ?? "",
            asset = p.Asset,
            readme,
            paths,
        };
    }

    /// <summary>El README entero si es corto; si no, sus líneas sobre el juego con dos de contexto a cada lado, hasta el tope.</summary>
    public static string Excerpt(string readme)
    {
        if (readme.Length <= ReadmeChars) return readme;
        var lines = readme.Split('\n');
        var keep = new SortedSet<int>();
        for (var i = 0; i < lines.Length; i++)
            if (GameWords().IsMatch(lines[i]))
                for (var j = Math.Max(0, i - 2); j <= Math.Min(lines.Length - 1, i + 2); j++) keep.Add(j);
        var text = new StringBuilder();
        var last = -2;
        foreach (var i in keep)
        {
            if (text.Length + lines[i].Length > ReadmeChars) break;
            if (i != last + 1) text.Append("…\n");
            text.Append(lines[i]).Append('\n');
            last = i;
        }
        return text.Length > 0 ? text.ToString() : readme[..ReadmeChars];
    }

    /// <summary>
    /// La respuesta como la pide el esquema de la receta, o null si no la cumple: <c>arg</c> con <c>{rom}</c> o <c>{romDir}</c>, <c>copy</c>
    /// con un nombre relativo y sin salir de la instalación, extensiones sin punto y en minúsculas.
    /// </summary>
    static JsonObject? ToGameFile(Answer a)
    {
        var formats = a.Formats.Select(f => f.Trim().TrimStart('.').ToLowerInvariant()).Where(f => Regex.IsMatch(f, "^[a-z0-9]+$")).Distinct().ToList();
        var json = new JsonObject { ["deliver"] = a.Deliver };
        switch (a.Deliver)
        {
            case "arg" when a.Args.Count > 0 && a.Args.Any(x => x.Contains("{rom}") || x.Contains("{romDir}")):
                json["args"] = new JsonArray([.. a.Args.Select(x => JsonValue.Create(x))]);
                break;
            case "copy" when a.CopyTo.Trim() is { Length: > 0 } to && !Path.IsPathRooted(to) && !to.Split('/', '\\').Contains(".."):
                json["copyTo"] = to.Replace('\\', '/');
                break;
            case "none" or "manual":
                break;
            default:
                return null;
        }
        if (formats.Count > 0) json["formats"] = new JsonArray([.. formats.Select(f => JsonValue.Create(f))]);
        return json;
    }

    /// <summary>Lo guarda en la receta, sin publicar, solo si la receta sigue cumpliendo su esquema.</summary>
    static bool Store(Database db, Port p, JsonObject gameFile)
    {
        p.Json["gameFile"] = gameFile;
        var text = p.Json.ToJsonString(Recipes.Indented).ReplaceLineEndings("\n") + "\n";
        using var doc = JsonDocument.Parse(text);
        if (Published.CheckRecipe(doc.RootElement).Count > 0) { p.Json.Remove("gameFile"); return false; }
        using var c = db.Open();
        Recipes.Store(c, doc.RootElement, text);
        return true;
    }

    /// <summary>Un aviso en la tarjeta de la receta (como los de «Emuladores»), con lo que propone Claude y por qué no es seguro.</summary>
    static void Notice(Database db, Port p, JsonObject gameFile, string reason)
    {
        using var c = db.Open();
        c.Execute("""
            DELETE FROM review_item WHERE entity = 'recipe' AND status = 'open'
              AND json_extract(data, '$.recipe') = @Id AND json_extract(data, '$.reason') = 'gameFile'
            """, new { p.Id });
        var data = new JsonObject
        {
            ["recipe"] = p.Id, ["reason"] = "gameFile", ["repo"] = p.Repo, ["pattern"] = gameFile.ToJsonString(),
            ["files"] = new JsonArray(JsonValue.Create(reason)),
        };
        c.Execute("INSERT INTO review_item (kind, entity, data, created_at) VALUES ('doubtful', 'recipe', @data, @now)",
            new { data = data.ToJsonString(), now = DateTime.UtcNow.ToString("O") });
    }

    static void Seen(Database db, Port p, string result, string? reason)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO gamefile_seen (recipe, asset, result, reason, checked_at) VALUES (@Id, @Asset, @result, @reason, @now)
            ON CONFLICT (recipe) DO UPDATE SET asset = excluded.asset, result = excluded.result, reason = excluded.reason, checked_at = excluded.checked_at
            """, new { p.Id, p.Asset, result, reason, now = DateTime.UtcNow.ToString("O") });
    }

    [GeneratedRegex(@"\brom\b|roms\b|dump|baserom|extract|\.(z64|n64|v64|iso|xex|xbe|cue|bin|chd|gba|nds|sfc|smc|nes|elf|pkg|nsp|xci|wad|rvz|nup)\b|game (file|data|files)|\bassets?\b|\.o2r|\.otr|command[ -]line|--[a-z]|usage|place|copy|put (your|the)|drag|folder|directory|original game|legally|own copy|install",
        RegexOptions.IgnoreCase)]
    private static partial Regex GameWords();

    [GeneratedRegex(@"(^|/)(readme|install|usage|building|faq)[^/]*$|^docs?/|extract|rom|asset|config|\.(ya?ml|toml|ini|json)$|(^|/)(main|cli|args|launcher|game)\.(c|cc|cpp|h|rs|py|cs|go)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UsefulPath();
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;
using Dapper;

namespace Otteretro.Tests;

/// <summary>Con un `claude` falso: F2.1 (valida, reintenta una vez y falla bien) y F2.2 (límite, tope y horario).</summary>
[Collection(nameof(ClaudeRunnerTests))] // Launch es estático: estas pruebas no corren en paralelo entre sí
public class ClaudeRunnerTests : IDisposable
{
    public record Double(int Value, string? Note);

    readonly List<(ProcessStartInfo Start, string Stdin)> calls = [];

    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> real = ClaudeRunner.Launch;

    readonly TempDir tmp = new();
    readonly Database db;

    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;

    /// <summary>Hoy a esa hora del equipo, en UTC.</summary>
    static DateTime Local(int hour, int minute = 0, int days = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddDays(days).AddHours(hour).AddMinutes(minute), TimeZoneInfo.Local);

    DateTime now = Local(3); // dentro del horario por defecto (00:00 a 08:00)

    public ClaudeRunnerTests()
    {
        ClaudeBudget.UtcNow = () => now;
        var config = new AppConfig { DataFolder = tmp["datos"] };
        Assert.Null(DataFolder.Prepare(config.DataFolder));
        db = Startup.PrepareData(config);
    }

    public void Dispose()
    {
        ClaudeRunner.Launch = real;
        ClaudeBudget.UtcNow = realNow;
        tmp.Dispose();
    }

    Task<ClaudeResult<Double>> Run(string prompt = "p", object? input = null, string? model = null) =>
        ClaudeRunner.Run<Double>(db, 7, "prueba", prompt, input ?? new { }, CancellationToken.None, model);

    /// <summary>El registro de uso (F2.3): resultado y tokens de entrada de cada intento.</summary>
    List<(string Result, long Input)> Usage()
    {
        using var c = db.Open();
        return c.Query<(string, long)>("SELECT result, input_tokens FROM claude_usage WHERE task_id = 7 AND prompt = 'prueba' ORDER BY id").ToList();
    }

    /// <summary>Un `claude` que responde, en orden, lo que se le pasa.</summary>
    void Fake(params ProcessOutput[] outputs) => ClaudeRunner.Launch = (start, stdin, ct) =>
    {
        calls.Add((start, stdin));
        return Task.FromResult(outputs[calls.Count - 1]);
    };

    static ProcessOutput Reply(object? structured, bool isError = false, string? result = null, double cost = 0.01) =>
        new(0, JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = isError,
            result,
            structured_output = structured,
            total_cost_usd = cost,
            usage = new { input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 100, cache_creation_input_tokens = 0, server_tool_use = new { } },
        }), "");

    [Fact]
    public async Task ValidReplyIsReadWithItsUsage()
    {
        Fake(Reply(new { value = 4, note = (string?)null }));

        var r = await Run("Duplica n.", new { n = 2 }, "haiku");

        Assert.Equal(new Double(4, null), r.Value);
        Assert.Equal(new ClaudeUsage(10, 5, 100, 0), r.Usage);
        Assert.Equal(1, r.Attempts);
        var (start, stdin) = Assert.Single(calls);
        var args = start.ArgumentList.ToList();
        Assert.Equal("{\"n\":2}", stdin);
        Assert.Equal("Duplica n.", args[args.IndexOf("-p") + 1]);
        Assert.Equal(ClaudeRunner.Tools, args[args.IndexOf("--tools") + 1]); // solo búsqueda y lectura web
        Assert.Contains("--restricted", args);
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]);
        var schema = JsonDocument.Parse(args[args.IndexOf("--json-schema") + 1]).RootElement;
        Assert.Equal(["value", "note"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("object", schema.GetProperty("type").GetString()); // claude rechaza ["object", "null"]
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task InvalidReplyIsAskedAgainOnce()
    {
        Fake(Reply(new { value = "cuatro", note = "x" }), Reply(new { value = 4, note = "x" }));

        var r = await Run();

        Assert.Equal(4, r.Value.Value);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(20, r.Usage.InputTokens); // se cuentan los dos intentos
        Assert.Equal([("invalid", 10L), ("ok", 10L)], Usage());
    }

    [Theory]
    [InlineData("""{"value":4,"note":"x","extra":1}""")] // sobra un campo
    [InlineData("""{"value":4}""")]                      // falta uno
    [InlineData("null")]                                 // sin salida estructurada
    public async Task TwoInvalidRepliesFail(string structured)
    {
        var bad = Reply(JsonSerializer.Deserialize<JsonElement>(structured));
        Fake(bad, bad, bad);

        var e = await Assert.ThrowsAsync<TaskException>(() => Run());

        Assert.Equal("tasks.claude.invalidOutput", e.Error.Code);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task ClaudeErrorFailsWithoutRetrying()
    {
        Fake(Reply(null, isError: true, result: "algo salió mal"), Reply(new { value = 4, note = "x" }));

        var e = await Assert.ThrowsAsync<TaskException>(() => Run());

        Assert.Equal("tasks.claude.failed", e.Error.Code);
        Assert.Equal("algo salió mal", e.Error.Params!["detail"]);
        Assert.Single(calls);
        Assert.Equal([("failed", 10L)], Usage());
    }

    [Fact]
    public async Task CrashWithoutJsonReportsStderr()
    {
        Fake(new ProcessOutput(1, "", "no autenticado\n"));

        var e = await Assert.ThrowsAsync<TaskException>(() => Run());

        Assert.Equal("no autenticado", e.Error.Params!["detail"]);
        Assert.Equal([("failed", 0L)], Usage());
    }

    [Fact]
    public async Task MissingClaudeIsReported()
    {
        ClaudeRunner.Launch = (_, _, _) => throw new Win32Exception(2);

        var e = await Assert.ThrowsAsync<TaskException>(() => Run());

        Assert.Equal("tasks.claude.notInstalled", e.Error.Code);
    }

    static readonly ProcessOutput Ok = Reply(new { value = 4, note = "x" });

    static ProcessOutput Limit(string text = "You've hit your session limit · resets 8:10am (America/Santiago)") =>
        Reply(null, isError: true, result: text, cost: 0);

    /// <summary>F2.2: al tocar el límite se espera hasta la hora que dice `claude`, sin volver a llamarlo mientras tanto.</summary>
    [Fact]
    public async Task LimitPausesUntilTheResetAndThenResumes()
    {
        Fake(Limit("You've hit your session limit · resets 5am (" + TimeZoneInfo.Local.Id + ")"), Ok);

        var e = await Assert.ThrowsAsync<ClaudePausedException>(() => Run());

        Assert.Equal(Local(5), e.Until);
        Assert.Equal("tasks.claude.limit", e.Reason.Code);
        Assert.Equal([("limit", 10L)], Usage());

        now = Local(4, 59);
        await Assert.ThrowsAsync<ClaudePausedException>(() => Run());
        Assert.Single(calls); // no se llamó a claude

        now = Local(5);
        Assert.Equal(4, (await Run()).Value.Value);
    }

    [Theory]
    [InlineData("You've hit your session limit · resets 8:10pm (America/Santiago)", "2026-10-31T10:00:00Z", "2026-10-31T23:10:00Z")]
    [InlineData("You've hit your 5-hour limit · resets 1:40pm (America/Santiago)", "2026-10-31T20:00:00Z", "2026-11-01T16:40:00Z")] // ya pasó hoy: mañana
    [InlineData("You've hit your weekly limit · resets Nov 3, 9am (America/Santiago)", "2026-10-31T10:00:00Z", "2026-11-03T12:00:00Z")]
    [InlineData("You've hit your weekly limit · resets Jan 2, 9am (UTC)", "2026-12-30T10:00:00Z", "2027-01-02T09:00:00Z")]
    public void ResetTimeIsRead(string text, string nowUtc, string expected)
    {
        var at = ClaudeBudget.ParseReset(text, DateTime.Parse(nowUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal), at);
    }

    [Fact]
    public async Task UnreadableResetWaitsAnHour()
    {
        Fake(Limit("Usage limit reached · resets soon"));

        var e = await Assert.ThrowsAsync<ClaudePausedException>(() => Run());

        Assert.Equal(now.AddHours(1), e.Until);
    }

    /// <summary>F2.2 y #154: fuera del horario no se llama a Claude, salvo con «Ejecutar ahora» hasta el próximo horario.</summary>
    [Fact]
    public async Task OutsideTheScheduleWaitsUnlessRunNow()
    {
        Fake(Ok);
        now = Local(14);

        var e = await Assert.ThrowsAsync<ClaudePausedException>(() => Run());
        Assert.Equal("tasks.claude.schedule", e.Reason.Code);
        Assert.Equal(Local(0, days: 1), e.Until);
        Assert.Empty(calls);

        ClaudeBudget.RunNow(db);
        Assert.Equal(4, (await Run()).Value.Value);
        Assert.Equal(Local(0, days: 1), DateTime.Parse(ClaudeBudget.Status(db).RunNowUntil!, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }

    [Fact]
    public async Task ScheduleCanCrossMidnight()
    {
        using (var c = db.Open())
        {
            ClaudeBudget.Set(c, ClaudeBudget.ScheduleStart, "22:00");
            ClaudeBudget.Set(c, ClaudeBudget.ScheduleEnd, "06:00");
        }
        Fake(Ok, Ok);
        now = Local(23);
        await Run();
        now = Local(7);
        await Assert.ThrowsAsync<ClaudePausedException>(() => Run());
        now = Local(5, days: 1);
        await Run();
    }

    /// <summary>
    /// F2.2 y #157: el límite de sesión enseña cuánto entra en una ventana (lo gastado en ella). De noche no hay tope;
    /// de día, con «Ejecutar ahora», al llegar al 50 % de eso se espera a que termine la ventana.
    /// </summary>
    [Fact]
    public async Task WindowCapOnlyAppliesToRunNowDuringTheDay()
    {
        var zone = TimeZoneInfo.Local.Id;
        Fake(Reply(new { value = 4, note = "x" }, cost: 1), Reply(new { value = 4, note = "x" }, cost: 1),
            Limit($"You've hit your session limit · resets 6am ({zone})"),
            Reply(new { value = 4, note = "x" }, cost: 1.5), Ok, // de noche pasa del 50 % sin parar
            Reply(new { value = 4, note = "x" }, cost: 1), Ok);

        now = Local(2);
        await Run();
        await Run();
        await Assert.ThrowsAsync<ClaudePausedException>(() => Run()); // entraron 2 USD en la ventana de 01:00 a 06:00

        now = Local(6);
        await Run();
        await Run();

        now = Local(12);
        ClaudeBudget.RunNow(db);
        await Run(); // 1 USD = 50 % de 2
        var e = await Assert.ThrowsAsync<ClaudePausedException>(() => Run());
        Assert.Equal("tasks.claude.windowCap", e.Reason.Code);
        Assert.Equal(Local(17), e.Until);
        Assert.Equal(6, calls.Count);
    }

    /// <summary>F2.9: sin tope marcado, «Ejecutar ahora» usa la ventana entera también de día.</summary>
    [Fact]
    public async Task WindowCapCanBeTurnedOff()
    {
        var zone = TimeZoneInfo.Local.Id;
        Fake(Reply(new { value = 4, note = "x" }, cost: 2), Limit($"You've hit your session limit · resets 6am ({zone})"),
            Reply(new { value = 4, note = "x" }, cost: 1.5), Ok);
        now = Local(2);
        await Run();
        await Assert.ThrowsAsync<ClaudePausedException>(() => Run()); // entran 2 USD por ventana

        Assert.Null(ClaudeBudget.Save(db, new(null, null, null, Capped: false)));
        Assert.False(ClaudeBudget.Status(db).Capped);
        now = Local(12);
        ClaudeBudget.RunNow(db);
        await Run(); // 1,5 USD: pasa del 50 % sin parar
        await Run();
        Assert.Equal(4, calls.Count);
    }

    /// <summary>F2.9: «Ejecutar ahora» lanza la carga; al empezar el horario se lanza sola, una vez por noche.</summary>
    [Fact]
    public void RunNowAndTheScheduleLaunchTheLoad()
    {
        now = Local(12);
        Assert.Null(ClaudeBudget.AutoStart(db)); // fuera del horario
        var load = ClaudeBudget.RunNow(db);
        Assert.Equal((InitialLoad.Kind, "queued"), (ClaudeBudget.Status(db).Task!.Kind, ClaudeBudget.Status(db).Task!.Status));
        Assert.Equal(load, ClaudeBudget.RunNow(db)); // no encola otra

        using (var c = db.Open()) c.Execute("UPDATE task SET status = 'done'");
        now = Local(0, days: 1).AddMinutes(1);
        var night = ClaudeBudget.AutoStart(db);
        Assert.NotNull(night);
        Assert.NotEqual(load, night);
        using (var c = db.Open()) c.Execute("UPDATE task SET status = 'done'");
        now = now.AddHours(2);
        Assert.Null(ClaudeBudget.AutoStart(db)); // ya se lanzó esta noche
        now = now.AddDays(1);
        Assert.NotNull(ClaudeBudget.AutoStart(db));
    }

    /// <summary>F2.2: la cola devuelve la tarea a la espera con su punto de control y no le cuenta el intento.</summary>
    [Fact]
    public async Task QueuePutsAPausedTaskBackWithItsCheckpoint()
    {
        var until = DateTime.UtcNow.AddHours(3);
        TaskQueue.Handlers["test-claude-pause"] = (run, ct) =>
        {
            run.Report(40, null, "lote 2");
            throw new ClaudePausedException(until, Api.ApiError.Of("tasks.claude.limit", ("at", until.ToString("O"))));
        };
        await using var f = new Fixture();
        var id = TaskQueue.Enqueue(f.Db, "test-claude-pause");

        await f.Queue.StartAsync(CancellationToken.None);
        for (var i = 0; i < 250 && f.Queue.List().FirstOrDefault(t => t.Id == id)?.Detail?.Code != "tasks.claude.limit"; i++) await Task.Delay(20);
        await f.Queue.StopAsync(CancellationToken.None);

        using var c = f.Db.Open();
        var row = c.QuerySingle<(string Status, string Data, string ResumeAt, long Attempts)>(
            "SELECT status, data, resume_at, attempts FROM task WHERE id = @id", new { id });
        Assert.Equal(("queued", "lote 2", until.ToString("O"), 0L), row);
    }
}

public class ClaudeFingerprintTests
{
    /// <summary>Listo cuando de F2.4: sin cambios no se pide; con un cambio, sí; «recalcular» lo vuelve a pedir.</summary>
    [Fact]
    public async Task OnlyChangedDataGoesToClaude()
    {
        await using var f = new Fixture();
        var zelda = new { saga = "Zelda", games = new[] { "Ocarina of Time", "Majora's Mask" } };

        Assert.True(ClaudeFingerprint.Changed(f.Db, "depurar", "saga:1", zelda));
        ClaudeFingerprint.Save(f.Db, "depurar", "saga:1", zelda);
        Assert.False(ClaudeFingerprint.Changed(f.Db, "depurar", "saga:1", zelda));
        Assert.True(ClaudeFingerprint.Changed(f.Db, "ruta-saga", "saga:1", zelda)); // cada prompt lleva la suya

        var more = new { saga = "Zelda", games = new[] { "Ocarina of Time", "Majora's Mask", "Echoes of Wisdom" } };
        Assert.True(ClaudeFingerprint.Changed(f.Db, "depurar", "saga:1", more));

        ClaudeFingerprint.Forget(f.Db, "depurar");
        Assert.True(ClaudeFingerprint.Changed(f.Db, "depurar", "saga:1", zelda));
    }
}

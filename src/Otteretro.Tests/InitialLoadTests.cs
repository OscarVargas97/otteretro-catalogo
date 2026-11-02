using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>F2.8 con un `claude` falso: por partes, depurar antes de preparar, y una pausa por límite que sigue sola desde su punto.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch y ClaudeBudget.UtcNow
public class InitialLoadTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<string> calls = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> real = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;
    // Ayer de noche: la hora de reanudar ya pasó para la cola (que usa la hora real) y Claude está en su horario.
    readonly DateTime now = TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddDays(-1).AddHours(3), TimeZoneInfo.Local);

    public InitialLoadTests()
    {
        // Al tocar el límite, el reloj salta a la hora de reanudar.
        ClaudeBudget.UtcNow = () =>
        {
            using var c = f.Db.Open();
            return c.ExecuteScalar<string?>("SELECT value FROM setting WHERE name = 'claude.pausedUntil'") is { } paused
                ? DateTime.Parse(paused, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() : now;
        };
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO saga (id, name) VALUES (1, 'The Legend of Zelda'), (2, 'Gran Turismo'), (3, 'Half-Life'), (4, 'Crash Bandicoot');
            INSERT INTO game (id, saga_id, name_es, original_name, year) VALUES
              (10, 1, 'Ocarina of Time', 'Ocarina of Time', 1998), (11, 1, 'Ocarina of Time 3D', 'Ocarina of Time 3D', 2011),
              (20, 2, 'Gran Turismo', 'Gran Turismo', 1997), (21, 2, 'Gran Turismo 2', 'Gran Turismo 2', 1999),
              (30, 3, 'Half-Life', 'Half-Life', 1998), (31, 3, 'Half-Life 2', 'Half-Life 2', 2004),
              (40, 4, 'Crash Bandicoot', 'Crash Bandicoot', 1996), (41, 4, 'Crash Bandicoot N. Sane Trilogy', 'Crash Bandicoot N. Sane Trilogy', 2017);
            INSERT INTO version (game_id, platform, year, igdb_id) VALUES
              (10, 'n64', 1998, 1), (11, '3ds', 2011, 2), (20, 'ps1', 1997, 3), (21, 'ps1', 1999, 4),
              (30, 'pc', 1998, 5), (31, 'pc', 2004, 6), (40, 'ps1', 1996, 7), (41, 'switch', 2018, 8);
            """);
    }

    public async ValueTask DisposeAsync()
    {
        ClaudeRunner.Launch = real;
        ClaudeBudget.UtcNow = realNow;
        await f.DisposeAsync();
    }

    static ProcessOutput Reply(object? value, bool error = false, string? result = null) => new(0, JsonSerializer.Serialize(new
    {
        is_error = error,
        result,
        structured_output = value,
        total_cost_usd = 0.01,
        usage = new { input_tokens = 100, output_tokens = 10, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
    }, JsonSerializerOptions.Web), "");

    [Fact]
    public async Task PartsGoInOrderCleanupFirstAndAPauseResumes()
    {
        ClaudeRunner.Launch = (start, stdin, ct) =>
        {
            var args = string.Join(' ', start.ArgumentList);
            var (kind, value) = args.Contains("mergeGameIds") ? ("cleanup", (object)new CatalogCleanup.Reply([], []))
                : args.Contains("rationale") ? ("routes", new SagaAnalysis.RoutesReply([]))
                : ("iconic", new SagaAnalysis.IconicReply([]));
            var names = new[] { ("Zelda", "zelda"), ("Crash", "crash"), ("Gran Turismo", "gt"), ("Half-Life", "hl") }
                .Where(n => stdin.Contains(n.Item1)).Select(n => n.Item2);
            calls.Add($"{kind}:{string.Join('+', names)}");
            if (calls.Count == 4) // la primera llamada de Sony: límite
                return Task.FromResult(Reply(null, true, $"You've hit your session limit · resets 4am ({TimeZoneInfo.Local.Id})"));
            return Task.FromResult(Reply(value));
        };

        var task = await f.Run((long)InitialLoad.Enqueue(f.Db)!);

        Assert.Equal("done", task.Status);
        // Crash tiene un juego en Switch: va con Nintendo, en el mismo lote que Zelda. Microsoft no tiene sagas.
        Assert.Equal([
            "cleanup:zelda", "routes:zelda+crash", "iconic:zelda+crash", // Zelda tiene repetidos posibles: se depura primero
            "routes:gt", "routes:gt", "iconic:gt", // se cortó por el límite y siguió desde ahí
            "routes:hl", "iconic:hl",
        ], calls);
        using var c = f.Db.Open();
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT count(*) FROM claude_usage WHERE result = 'limit'"));
    }

    [Fact]
    public void UnknownPartIsRefused() => Assert.Null(InitialLoad.Enqueue(f.Db, "sega"));
}

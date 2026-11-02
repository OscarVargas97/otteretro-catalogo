using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>F2.5 con un `claude` falso: cada prompt guarda su resultado en Revisar, descarta lo inventado y no repite lo que no cambió.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch y ClaudeBudget.UtcNow
public class SagaAnalysisTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<string> calls = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> real = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;

    public SagaAnalysisTests()
    {
        ClaudeBudget.UtcNow = () => TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(3), TimeZoneInfo.Local); // de noche
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO saga (id, name) VALUES (1, 'The Legend of Zelda');
            INSERT INTO game (id, saga_id, name_es, original_name, year) VALUES
              (10, 1, 'Ocarina of Time', 'Ocarina of Time', 1998), (11, 1, 'Majora''s Mask', 'Majora''s Mask', 2000);
            INSERT INTO version (id, game_id, platform, kind, year, igdb_id) VALUES
              (100, 10, 'n64', 'original', 1998, 1), (101, 10, '3ds', 'original', 2011, 2), (110, 11, 'n64', 'original', 2000, 3);
            INSERT INTO field_value (entity, entity_id, field, value, source) VALUES ('game', 10, 'summary', 'Link wakes up.', 'igdb');
            """);
    }

    public async ValueTask DisposeAsync()
    {
        ClaudeRunner.Launch = real;
        ClaudeBudget.UtcNow = realNow;
        await f.DisposeAsync();
    }

    static readonly SagaAnalysis.Criterion Good = new(4, "Bien");
    static readonly SagaAnalysis.Source Wiki = new("Zelda Wiki", "https://zeldawiki.wiki");

    /// <summary>Un `claude` que responde según el esquema que le piden (el de cada prompt).</summary>
    void Fake() => ClaudeRunner.Launch = (start, stdin, ct) =>
    {
        var args = string.Join(' ', start.ArgumentList);
        object reply =
            args.Contains("rationale") ? new SagaAnalysis.RoutesReply([new(1, [
                new("release", "Por fecha", [new(10, null, "Primero"), new(11, null, "Después")], [Wiki]),
                new("community", "Así se recomienda", [new(10, 101, "El de 3DS se ve mejor")], [Wiki])])])
            : args.Contains("iconic") ? new SagaAnalysis.IconicReply([new(1, true, [10], "Cambió el 3D", [Wiki]), new(99, true, [], "Inventada", [])])
            : args.Contains("bestVersionId") ? new SagaAnalysis.BestReply([new(10, 101, "Mejor hoy", [new(100, Good, Good, Good, new(5, "Es el original")), new(101, Good, Good, Good, Good)])])
            : args.Contains("\"text\"") ? new SagaAnalysis.TranslateReply([new(10, "Link despierta.")])
            : new SagaAnalysis.ClassifyReply([new(101, "remake", "Gráficos nuevos"), new(100, "original", "Bien así")]);
        calls.Add(args);
        return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new
        {
            is_error = false,
            structured_output = reply,
            total_cost_usd = 0.01,
            usage = new { input_tokens = 100, output_tokens = 10, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
        }, JsonSerializerOptions.Web), ""));
    };

    List<(string Entity, long EntityId, string Prompt)> Pending()
    {
        using var c = f.Db.Open();
        return c.Query<(string, long, string)>("""
            SELECT entity, entity_id, json_extract(data, '$.prompt') FROM review_item WHERE kind = 'pending_approval' AND status = 'open' ORDER BY id
            """).ToList();
    }

    /// <summary>F2.15: reevaluar lo icónico (de todo o de una consola) solo le pregunta eso a Claude, aunque nada haya cambiado.</summary>
    [Fact]
    public async Task ReevaluatingIconicAsksOnlyThat()
    {
        Fake();
        Assert.Null(SagaAnalysis.EnqueueIconic(f.Db, "ps2")); // sin sagas en esa consola
        Assert.Equal("done", (await f.Run(SagaAnalysis.EnqueueIconic(f.Db, "n64")!.Value)).Status);
        Assert.Single(calls);
        Assert.Contains("iconic", calls[0]);
        Assert.Equal("done", (await f.Run(SagaAnalysis.EnqueueIconic(f.Db)!.Value)).Status);
        Assert.Equal(2, calls.Count); // otra vez, aunque los datos no cambiaron
    }

    [Fact]
    public async Task EachPromptSavesItsResultForReview()
    {
        Fake();

        Assert.Equal("done", (await f.Run(SagaAnalysis.Enqueue(f.Db))).Status);

        Assert.Equal(5, calls.Count);
        Assert.Equal([
            ("saga", 1, SagaAnalysis.Routes),
            ("saga", 1, SagaAnalysis.Iconic),        // la saga 99 no venía: se descarta
            ("game", 10, SagaAnalysis.BestVersion),  // Majora tiene una sola versión: no se compara
            ("version", 101, SagaAnalysis.Classify), // la 100 ya era original: no es un cambio
            ("game", 10, SagaAnalysis.Translate),
        ], Pending());

        // Sin cambios en los datos, otra pasada no llama a Claude; con un juego nuevo, solo lo de su saga.
        Assert.Equal("done", (await f.Run(SagaAnalysis.Enqueue(f.Db))).Status);
        Assert.Equal(5, calls.Count);
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO game (id, saga_id, name_es, year) VALUES (12, 1, 'Wind Waker', 2002)");
        Assert.Equal("done", (await f.Run(SagaAnalysis.Enqueue(f.Db, [1]))).Status);
        Assert.Equal(7, calls.Count); // rutas e icónico; las versiones y la descripción no cambiaron
        Assert.Equal(5, Pending().Count); // los nuevos reemplazan a los abiertos
    }
}

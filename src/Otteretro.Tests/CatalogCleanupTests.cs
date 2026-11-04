using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Otteretro.App;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>F2.7 con un `claude` falso: marca solo lo dudoso, aplica los cambios, no repite lo que no cambió y deshace.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch y ClaudeBudget.UtcNow
public class CatalogCleanupTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<List<string>> calls = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> real = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;

    public CatalogCleanupTests()
    {
        ClaudeBudget.UtcNow = () => TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(3), TimeZoneInfo.Local); // de noche
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO saga (id, name) VALUES (1, 'The Legend of Zelda'), (2, 'Half-Life'), (3, 'Okami');
            INSERT INTO game (id, saga_id, name_es, original_name, year) VALUES
              (10, 1, 'The Legend of Zelda: Ocarina of Time', 'The Legend of Zelda: Ocarina of Time', 1998),
              (11, 1, 'The Legend of Zelda: Ocarina of Time 3D', 'The Legend of Zelda: Ocarina of Time 3D', 2011),
              (12, 1, 'Freshly-Picked Tingle''s Rosy Rupeeland', 'Freshly-Picked Tingle''s Rosy Rupeeland', 2006),
              (20, NULL, 'The Legend of Zelda: Echoes of Wisdom', 'The Legend of Zelda: Echoes of Wisdom', 2024),
              (21, NULL, 'Tetris', 'Tetris', 1984),
              (30, 2, 'Half-Life', 'Half-Life', 1998), (31, 2, 'Half-Life 2', 'Half-Life 2', 2004),
              (40, 3, 'Okami', 'Okami', 2006);
            INSERT INTO version (game_id, platform, year, igdb_id) VALUES
              (10, 'n64', 1998, 1), (11, '3ds', 2011, 2), (12, 'ds', 2006, 3), (20, 'switch', 2024, 4),
              (30, 'pc', 1998, 5), (31, 'pc', 2004, 6), (40, 'ps2', 2006, 7);
            INSERT INTO field_value (entity, entity_id, field, value, source) VALUES ('saga', 1, 'name', 'The Legend of Zelda', 'igdb');
            """);
    }

    public async ValueTask DisposeAsync()
    {
        ClaudeRunner.Launch = real;
        ClaudeBudget.UtcNow = realNow;
        await f.DisposeAsync();
    }

    /// <summary>Un `claude` que responde, en orden, estas respuestas.</summary>
    void Fake(params CatalogCleanup.Reply[] replies) => ClaudeRunner.Launch = (start, stdin, ct) =>
    {
        calls.Add([.. start.ArgumentList, stdin]);
        var reply = replies[calls.Count - 1];
        return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new
        {
            is_error = false,
            structured_output = reply,
            total_cost_usd = 0.01,
            usage = new { input_tokens = 100, output_tokens = 10, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
        }, JsonSerializerOptions.Web), ""));
    };

    T Scalar<T>(string sql)
    {
        using var c = f.Db.Open();
        return c.ExecuteScalar<T>(sql)!;
    }

    [Fact]
    public void OnlyDoubtfulSagasAreMarked()
    {
        var cases = CatalogCleanup.Cases(f.Db, null);

        var zelda = Assert.Single(cases); // Half-Life está bien y Okami tiene un solo juego
        Assert.Equal(1, zelda.Id);
        Assert.Equal(["similarNames", "looseCandidates"], zelda.Reasons);
        Assert.Equal("20|The Legend of Zelda: Echoes of Wisdom|2024|switch", Assert.Single(zelda.Loose).Line); // Tetris no
        Assert.Equal(1, CatalogCleanup.Measure(f.Db).Calls);

        // Con llamadas hechas, la estimación usa lo que gastó cada una (F2.7: el primer lote ajusta la estimación).
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO claude_usage (prompt, model, duration_ms, result, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, cost_usd, created_at)
                VALUES ('depurar-sagas', 'haiku', 1, 'ok', 10000, 20000, 4000, 0, 0.01, '2026-10-31'), ('depurar-sagas', 'haiku', 1, 'ok', 20000, 30000, 0, 0, 0.01, '2026-10-31')
                """);
        Assert.Equal(42000, CatalogCleanup.Measure(f.Db).Tokens);
    }

    /// <summary>#53: dos sagas que se llaman igual (la colección y la franquicia de IGDB) se marcan juntas, aunque una tenga un solo juego.</summary>
    [Fact]
    public void SagasWithTheSameNameAreMarkedTogether()
    {
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO saga (id, name) VALUES (4, 'Okamí'), (5, 'Half Life');
                INSERT INTO game (id, saga_id, name_es, year) VALUES (41, 4, 'Okamiden', 2010), (50, 5, 'Half-Life: Alyx', 2020);
                INSERT INTO claude_fingerprint (prompt, key, input_hash, updated_at) VALUES ('depurar-sagas', 'saga:2', 'x', '2026-11-01'), ('depurar-sagas', 'saga:5', 'x', '2026-11-01');
                """);

        var cases = CatalogCleanup.Cases(f.Db, null);

        // Okami (1 juego) y Okamí van juntas; Half-Life y Half Life ya las vio Claude: no se repiten.
        Assert.Equal([1L, 3L, 4L], cases.Select(x => x.Id));
        Assert.Contains("sameName", cases.Single(x => x.Id == 3).Reasons);
        Assert.DoesNotContain("sameName", cases.Single(x => x.Id == 1).Reasons);
    }

    [Fact]
    public async Task ChangesAreAppliedNotRepeatedAndUndone()
    {
        Fake(new CatalogCleanup.Reply([
            new("merge", "El 3D es un remake del mismo juego", KeepGameId: 10, MergeGameIds: [11]),
            new("move", "Es de la saga", GameIds: [20], ToSagaId: 1),
            new("move", "Tingle tiene saga propia", GameIds: [12], NewSagaName: "Tingle"),
            new("rename", "Nombre corto", SagaId: 1, Name: "Zelda"),
            new("move", "Inventado", GameIds: [999], ToSagaId: 1), // fuera del lote: se descarta
            new("move", "Otra saga", GameIds: [30], ToSagaId: 1),  // Half-Life no venía en el lote
        ], []));

        var task = await f.Run(CatalogCleanup.Enqueue(f.Db));

        Assert.Equal("done", task.Status);
        Assert.Contains("haiku", calls[0]);
        Assert.Contains("Echoes of Wisdom", calls[0][^1]);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM game WHERE id = 11"));
        Assert.Equal(10, Scalar<long>("SELECT game_id FROM version WHERE igdb_id = 2"));
        Assert.Equal(1, Scalar<long>("SELECT saga_id FROM game WHERE id = 20"));
        Assert.Equal("Tingle", Scalar<string>("SELECT s.name FROM game g JOIN saga s ON s.id = g.saga_id WHERE g.id = 12"));
        Assert.Equal("Zelda", Scalar<string>("SELECT name FROM saga WHERE id = 1"));
        Assert.Equal(2, Scalar<long>("SELECT saga_id FROM game WHERE id = 30"));
        Assert.Equal(4, Scalar<long>("SELECT count(*) FROM learning WHERE kind = 'rule' AND undone = 0"));

        // F2.10: el registro de la tarea dice qué hizo, lo más nuevo arriba; F2.11: limpiar la borra con su registro.
        var log = TaskQueue.ReadLog(f.Db, task.Id).Select(l => l.Message.Code).Reverse().ToList();
        Assert.Equal(["tasks.log.started", "tasks.log.claude", "tasks.log.change.merge", "tasks.log.change.move", "tasks.log.change.move",
            "tasks.log.change.rename", "tasks.log.done"], log);
        Assert.Equal(1, TaskQueue.Clear(f.Db));
        Assert.Empty(TaskQueue.ReadLog(f.Db, task.Id));

        // Sin cambios en los datos, otra pasada no llama a Claude.
        var again = await f.Run(CatalogCleanup.Enqueue(f.Db));
        Assert.Equal("done", again.Status);
        Assert.Single(calls);
        // #172: el ✕ de una tarea terminada borra solo esa.
        Assert.Equal(0, TaskQueue.Clear(f.Db, again.Id + 1));
        Assert.Equal(1, TaskQueue.Clear(f.Db, again.Id));

        foreach (var id in Enumerable.Range(1, 4).Reverse()) Assert.True(CatalogCleanup.Undo(f.Db, id));
        Assert.False(CatalogCleanup.Undo(f.Db, 1));

        Assert.Equal("The Legend of Zelda: Ocarina of Time 3D", Scalar<string>("SELECT name_es FROM game WHERE id = 11"));
        Assert.Equal(11, Scalar<long>("SELECT game_id FROM version WHERE igdb_id = 2"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM game WHERE id = 20 AND saga_id IS NOT NULL"));
        Assert.Equal(1, Scalar<long>("SELECT saga_id FROM game WHERE id = 12"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM saga WHERE name = 'Tingle'"));
        Assert.Equal("The Legend of Zelda", Scalar<string>("SELECT name FROM saga WHERE id = 1"));
    }

    /// <summary>F2.12: Aprendizajes dice cada cambio con nombres; «Corregir» deshace y la nota va a Claude como regla de la saga.</summary>
    [Fact]
    public async Task LearningsReadClearlyAndCorrectionsBecomeRules()
    {
        Fake(new CatalogCleanup.Reply([
            new("merge", "Remake", KeepGameId: 10, MergeGameIds: [11]),
            new("move", "Tingle tiene saga propia", GameIds: [12], NewSagaName: "Tingle"),
            new("rename", "Nombre corto", SagaId: 1, Name: "Zelda"),
        ], []), new CatalogCleanup.Reply([], []));
        var task = await f.Run(CatalogCleanup.Enqueue(f.Db));

        var page = ClaudeReview.Learnings(f.Db, null, null, 0, 60).Items.ToDictionary(l => l.Change);
        Assert.Equal(3, ClaudeReview.Page(f.Db).ActiveLearnings);
        // Por tramos (#177): filtro por tipo, búsqueda en los nombres en claro y el total de lo que coincide.
        Assert.Equal((1, 3), (ClaudeReview.Learnings(f.Db, null, null, 1, 1).Items.Count, ClaudeReview.Learnings(f.Db, null, null, 1, 1).Total));
        Assert.Equal("merge", Assert.Single(ClaudeReview.Learnings(f.Db, "merge", null, 0, 60).Items).Change);
        Assert.Equal("move", Assert.Single(ClaudeReview.Learnings(f.Db, null, "tingle", 0, 60).Items).Change);
        Assert.Equal(("The Legend of Zelda: Ocarina of Time", "The Legend of Zelda: Ocarina of Time 3D (2011)"), (page["merge"].Kept, Assert.Single(page["merge"].Games)));
        Assert.Equal(["Zelda"], page["move"].From);
        Assert.Equal(("new", "Tingle"), (page["move"].ToKind, page["move"].To));
        Assert.Equal(("The Legend of Zelda", "Zelda", 1L), (page["rename"].NameBefore, page["rename"].NameAfter, page["rename"].SagaId));
        Assert.All(page.Values, l => Assert.Equal(task.Id, l.TaskId));

        Assert.Equal("claude.badNote", CatalogCleanup.Correct(f.Db, page["move"].Id, " ")?.Code);
        Assert.Null(CatalogCleanup.Correct(f.Db, page["move"].Id, "Tingle es parte de Zelda"));
        Assert.Equal(1, Scalar<long>("SELECT saga_id FROM game WHERE id = 12")); // se deshizo
        var after = ClaudeReview.Learnings(f.Db, "move", null, 0, 60).Items.Single();
        Assert.Equal(2, ClaudeReview.Page(f.Db).ActiveLearnings);
        Assert.True(after.Undone);
        Assert.Equal(["Tingle es parte de Zelda"], after.Rules);

        // La saga vuelve a ser un caso y la regla va en la entrada de Claude.
        Assert.Contains("personRules", Assert.Single(CatalogCleanup.Cases(f.Db, null)).Reasons);
        await f.Run(CatalogCleanup.Enqueue(f.Db));
        Assert.Contains("Tingle es parte de Zelda", calls[1][^1]);
    }

    [Fact]
    public async Task UnsureSagasGetASecondLookWithSonnet()
    {
        Fake(new CatalogCleanup.Reply([], [1]), new CatalogCleanup.Reply([], []));

        Assert.Equal("done", (await f.Run(CatalogCleanup.Enqueue(f.Db))).Status);

        Assert.Equal(2, calls.Count);
        Assert.Contains("haiku", calls[0]);
        Assert.Contains("sonnet", calls[1]);
    }
}

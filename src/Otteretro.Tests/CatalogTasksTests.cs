using Dapper;
using Otteretro.App;
using Otteretro.Catalog;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>#58: cada tarea del catálogo por separado, con su interruptor del horario y «Ejecutar».</summary>
public class CatalogTasksTests : IAsyncDisposable
{
    readonly Fixture f = new();

    public ValueTask DisposeAsync() => f.DisposeAsync();

    List<(string Kind, string? Data)> Queued()
    {
        using var c = f.Db.Open();
        return c.Query<(string, string?)>("SELECT kind, data FROM task WHERE status = 'queued' ORDER BY id").ToList();
    }

    [Fact]
    public void TheyAreListedInTheNightOrder()
    {
        var list = CatalogTasks.List(f.Db);
        Assert.Equal(["igdb", "codes", "ports", "portSearch", "emulators", "cleanup", "iconic", "routes", "best", "translate", "images"], list.Select(t => t.Id));
        Assert.Equal(7, list.Count(t => t.Model is not null)); // con Claude
        Assert.All(list, t => Assert.True(t.Auto));
        Assert.Equal(7, list[0].Days);
    }

    /// <summary>«Ejecutar» en Rutas encola solo ese paso, una vez; queda como su última ejecución.</summary>
    [Fact]
    public void RunQueuesOnlyThatStep()
    {
        Assert.True(CatalogTasks.Run(f.Db, "routes"));
        Assert.True(CatalogTasks.Run(f.Db, "routes")); // ya está en la cola: no se duplica
        Assert.False(CatalogTasks.Run(f.Db, "nada"));

        var (kind, data) = Assert.Single(Queued());
        Assert.Equal(SagaAnalysis.Kind, kind);
        Assert.True(SagaAnalysis.IsStep(data, SagaAnalysis.Routes));
        Assert.False(SagaAnalysis.IsStep(data, SagaAnalysis.Iconic));
        var list = CatalogTasks.List(f.Db);
        Assert.Equal("queued", list.Single(t => t.Id == "routes").LastStatus);
        Assert.Null(list.Single(t => t.Id == "iconic").LastStatus);
    }

    /// <summary>Decisión 284: de noche corren las encendidas, en orden; las apagadas no.</summary>
    [Fact]
    public void NightlyRunsTheEnabledOnesInOrder()
    {
        Assert.True(CatalogTasks.Save(f.Db, "routes", new(Auto: false)));
        Assert.True(CatalogTasks.Save(f.Db, "translate", new(Auto: false)));
        Assert.True(CatalogTasks.Save(f.Db, "igdb", new(Days: 3)));

        Assert.NotNull(CatalogTasks.Nightly(f.Db));

        Assert.Equal(["igdb.refresh", GameCodes.Kind, Ports.Kind, PortSearch.Kind, EmulatorCheck.Kind, CatalogCleanup.Kind, "iconico", "mejor-version", "sgdb.images"],
            Queued().Select(q => q.Kind == SagaAnalysis.Kind ? (SagaAnalysis.IsStep(q.Data, SagaAnalysis.Iconic) ? "iconico" : "mejor-version") : q.Kind));
        var list = CatalogTasks.List(f.Db);
        Assert.False(list.Single(t => t.Id == "routes").Auto);
        Assert.Equal(3, list[0].Days);

        Assert.Null(CatalogTasks.Nightly(f.Db)); // todo sigue en la cola: no se encola de nuevo
    }

    [Fact]
    public void MonthCostCountsItsPrompts()
    {
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO claude_usage (task_id, prompt, model, duration_ms, result, cost_usd, created_at)
                VALUES (1, 'ruta-saga', 'sonnet', 1, 'ok', 0.5, @now), (1, 'ruta-saga', 'sonnet', 1, 'ok', 0.25, @now), (1, 'iconico', 'sonnet', 1, 'ok', 9, '2000-01-01')
                """, new { now = DateTime.UtcNow.ToString("O") });
        var routes = CatalogTasks.List(f.Db).Single(t => t.Id == "routes");
        Assert.Equal((2, 0.75), (routes.MonthCalls, routes.MonthCost));
        Assert.Equal(0, CatalogTasks.List(f.Db).Single(t => t.Id == "iconic").MonthCalls);
    }

    /// <summary>#75: la última ejecución vive aparte de la cola; «Limpiar terminadas» no la borra.</summary>
    [Fact]
    public async Task TheLastRunSurvivesClearingTheQueue()
    {
        CatalogTasks.Run(f.Db, "images");
        long id;
        using (var c = f.Db.Open()) id = c.ExecuteScalar<long>("SELECT id FROM task WHERE kind = 'sgdb.images'");
        var done = await f.Run(id); // sin clave de SteamGridDB termina con error
        Assert.Equal("error", done.Status);

        Assert.Equal(1, TaskQueue.Clear(f.Db));
        var images = CatalogTasks.List(f.Db).Single(t => t.Id == "images");
        Assert.Equal("error", images.LastStatus);
        Assert.NotNull(images.LastAt);
        Assert.Null(CatalogTasks.List(f.Db).Single(t => t.Id == "translate").LastStatus);
    }

    /// <summary>#76: cada tarea cuenta lo que le falta con el criterio de lo que encola; sin pendiente contable, null.</summary>
    [Fact]
    public void EachTaskCountsWhatIsMissing()
    {
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO saga (id, name) VALUES (1, 'Zelda');
                INSERT INTO game (id, saga_id, name_es, original_name, year, popularity) VALUES (10, 1, 'Ocarina', 'Ocarina of Time', 1998, 500), (11, 1, 'Majora', 'Majora', 2000, 500);
                INSERT INTO version (id, game_id, platform, kind, year) VALUES (100, 10, 'n64', 'original', 1998), (101, 10, '3ds', 'original', 2011), (110, 11, 'n64', 'original', 2000);
                INSERT INTO field_value (entity, entity_id, field, value, source) VALUES ('game', 10, 'summary', 'Link wakes up.', 'igdb');
                INSERT INTO version_code (version_id, code) VALUES (110, 'NUS-NZSE');
                INSERT INTO catalog.image (game_id, kind, url) VALUES (11, 'cover', 'x'), (11, 'hero', 'x'), (11, 'logo', 'x');
                INSERT INTO port (console, game, project, kind, status, link, source, added) VALUES ('n64', 'Ocarina', 'Shipwright', 'decomp', 'complete', 'https://x', 'extra', '2026-11-01');
                """);
        int? Pending(string id) => CatalogTasks.PendingCounts(f.Db).TryGetValue(id, out var n) ? n : null;

        Assert.Equal(2, Pending("codes")); // 3 versiones, 1 con código
        Assert.Equal(1, Pending("ports"));
        Assert.Equal(1, Pending("images")); // el juego 11 tiene las 3
        Assert.Equal(1, Pending("translate"));
        Assert.Equal(1, Pending("iconic")); // popularidad en la franja dudosa: va a Claude
        Assert.Equal(1, Pending("best")); // originales de años distintos: se discute
        Assert.Equal(0, Pending("routes")); // la saga no es icónica
        Assert.Null(Pending("igdb"));
        Assert.Null(Pending("portSearch"));
        Assert.Null(Pending("cleanup"));

        ClaudeFingerprint.Save(f.Db, SagaAnalysis.Translate, "game:10", new { id = 10L, name = "Ocarina of Time", summary = "Link wakes up." });
        Assert.Equal(0, Pending("translate")); // «Al día»
        Assert.Null(CatalogTasks.List(f.Db).Single(t => t.Id == "igdb").DueAt); // nunca se actualizó: toca ya
    }
}

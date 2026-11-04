using Dapper;
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
        Assert.Equal(["igdb", "codes", "cleanup", "routes", "iconic", "best", "translate", "images"], list.Select(t => t.Id));
        Assert.Equal(5, list.Count(t => t.Model is not null)); // con Claude
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

        Assert.Equal(["igdb.refresh", GameCodes.Kind, CatalogCleanup.Kind, "iconico", "mejor-version", "sgdb.images"],
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
}

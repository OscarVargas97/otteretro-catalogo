using System.Diagnostics;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Otteretro.App;
using Otteretro.Catalog;
using Xunit.Abstractions;

namespace Otteretro.Tests;

/// <summary>
/// Fluidez primero (#173, #104): cada consulta que usa una pantalla responde en menos de 200 ms con un catálogo del tamaño
/// del real (unas 5.800 sagas, 50.000 juegos —30.000 sin saga—, 118.000 versiones y 25.000 portadas).
/// </summary>
[Collection(nameof(FluidityTests))] // sola: con otras pruebas en paralelo los tiempos no dicen nada
public class FluidityTests(ITestOutputHelper output) : IAsyncLifetime
{
    const int BudgetMs = 200;

    /// <summary>Las que se pasan del presupuesto. #106 las dejó todas dentro; ninguna nueva puede sumarse.</summary>
    static readonly HashSet<string> OverBudget = [];

    readonly Fixture f = new();
    WebApplication app = null!;
    HttpClient http = null!;

    public async Task InitializeAsync()
    {
        using (var c = f.Db.Open())
        {
            c.Execute("""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5800)
                INSERT INTO saga (id, name, iconic) SELECT i, 'Saga ' || i, i % 50 = 0 FROM n;

                -- 20.000 juegos en sagas (los primeros 400 de la saga 1, para tener una saga grande) y 30.000 sin saga.
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 50000)
                INSERT INTO game (id, saga_id, name_es, original_name, year, igdb_id, popularity)
                SELECT i, CASE WHEN i <= 400 THEN 1 WHEN i <= 20000 THEN 2 + i % 5799 END,
                       'Juego ' || i, 'Game ' || i, 1980 + i % 45, i, i % 997 FROM n;

                -- Dos o tres versiones por juego. Como en el catálogo real, los juegos de una saga comparten consolas (~4 consolas por saga).
                CREATE TEMP TABLE p AS SELECT id, row_number() OVER (ORDER BY rowid) - 1 AS k FROM console;
                INSERT INTO version (game_id, platform, kind, year, igdb_id)
                SELECT g.id, p.id, 'original', g.year, g.id FROM game g JOIN p ON p.k = coalesce(g.saga_id, g.id) % (SELECT count(*) FROM p);
                INSERT INTO version (game_id, platform, kind, year, igdb_id)
                SELECT g.id, p.id, 'remaster', g.year + 5, g.id FROM game g JOIN p ON p.k = (coalesce(g.saga_id, g.id) * 7 + 3) % (SELECT count(*) FROM p)
                WHERE NOT EXISTS (SELECT 1 FROM version v WHERE v.game_id = g.id AND v.platform = p.id);
                INSERT INTO version (game_id, platform, kind, year, igdb_id)
                SELECT g.id, 'pc', 'original', g.year, g.id FROM game g
                WHERE g.id % 3 = 0 AND NOT EXISTS (SELECT 1 FROM version v WHERE v.game_id = g.id AND v.platform = 'pc');

                -- Tres valores con su fuente por juego, como deja la carga de IGDB.
                INSERT INTO field_value (entity, entity_id, field, value, source)
                SELECT 'game', id, 'name_es', name_es, 'igdb' FROM game
                UNION ALL SELECT 'game', id, 'original_name', original_name, 'igdb' FROM game
                UNION ALL SELECT 'game', id, 'year', year, 'igdb' FROM game;

                -- Lo tuyo: 150 juegos de Steam y uno en curso.
                INSERT INTO source (kind, store, store_id, name, playtime, last_played, version_id)
                SELECT 'store', 'steam', v.id, 'Juego', v.id % 900, '2026-10-01T00:00:00Z', v.id FROM version v WHERE v.platform = 'pc' LIMIT 150;
                INSERT INTO progress (game_id, status) VALUES (1, 'playing');
                """);
        }
        // 25.000 portadas (vacías: solo cuenta que existan).
        var images = SteamGridDb.Folder(f.Db)!;
        Directory.CreateDirectory(images);
        for (var id = 1; id <= 25000; id++) File.Create(Path.Combine(images, $"{id * 2}-cover.png")).Dispose();

        app = LocalServer.Create(f.Session, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        http = app.GetTestClient();
        http.BaseAddress = new Uri("http://127.0.0.1/");
        http.DefaultRequestHeaders.Add(LocalServer.KeyHeader, f.Session.Key);
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await f.DisposeAsync();
    }

    /// <summary>Lo que piden Inicio, Sagas, una saga, una ficha, Colección, Revisar, la búsqueda, las tareas y cada pestaña de Ajustes.</summary>
    static readonly string[] Urls =
    [
        "/api/state", "/api/notices", "/api/review", "/api/review/count", "/api/learnings", "/api/learnings?q=zelda", "/api/tasks",
        "/api/sagas", "/api/sagas?sort=recognized", "/api/sagas?filter=owned", "/api/sagas?filter=exclusive&company=Nintendo",
        "/api/sagas?platform=switch", "/api/consoles",
        "/api/sagas/1", "/api/sagas/1/routes", "/api/sagas/0", "/api/sagas/0/games?offset=600",
        "/api/games/1", "/api/games/1/compare",
        "/api/search?q=juego%2012",
        "/api/data-folder", "/api/credentials", "/api/consoles/settings", "/api/claude", "/api/claude/usage?page=1", "/api/claude/automation",
    ];

    [Fact]
    public async Task EveryScreenQueryAnswersInUnder200Ms()
    {
        var slow = new List<string>();
        foreach (var url in Urls)
        {
            var response = await http.GetAsync(url); // la primera calienta la conexión y el plan de la consulta
            Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
            var times = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                (await http.GetAsync(url)).EnsureSuccessStatusCode();
                times.Add(watch.ElapsedMilliseconds);
            }
            var median = times.Order().ElementAt(1);
            output.WriteLine($"{median,6} ms  {url}");
            if (median > BudgetMs) slow.Add(url);
        }
        foreach (var done in OverBudget.Except(slow)) output.WriteLine($"Ya cumple, sácala de OverBudget: {done}");
        Assert.True(slow.All(OverBudget.Contains), $"Pasan de {BudgetMs} ms y no estaban: [{string.Join(", ", slow.Except(OverBudget))}]");
    }
}

[CollectionDefinition(nameof(FluidityTests), DisableParallelization = true)]
public class FluidityCollection;

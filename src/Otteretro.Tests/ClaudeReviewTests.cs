using System.Text.Json;
using Dapper;
using Otteretro.Claude;
using A = Otteretro.Claude.SagaAnalysis;

namespace Otteretro.Tests;

/// <summary>F2.6: aprobar aplica cada resultado de Claude a sus tablas; rechazar no toca nada; Inicio dice el siguiente juego.</summary>
public class ClaudeReviewTests : IAsyncDisposable
{
    readonly Fixture f = new();

    public ClaudeReviewTests()
    {
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO saga (id, name) VALUES (1, 'The Legend of Zelda');
            INSERT INTO game (id, saga_id, name_es, year) VALUES (10, 1, 'Ocarina of Time', 1998), (11, 1, 'Majora''s Mask', 2000);
            INSERT INTO version (id, game_id, platform, kind, year, igdb_id) VALUES
              (100, 10, 'n64', 'original', 1998, 1), (101, 10, '3ds', 'original', 2011, 2), (110, 11, 'n64', 'original', 2000, 3);
            """);
    }

    public async ValueTask DisposeAsync() => await f.DisposeAsync();

    long Item(string prompt, string entity, long id, object result)
    {
        using var c = f.Db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO review_item (kind, entity, entity_id, data, created_at) VALUES ('pending_approval', @entity, @id, @data, '2026-10-31') RETURNING id
            """, new { entity, id, data = JsonSerializer.Serialize(new { prompt, result }, JsonSerializerOptions.Web) });
    }

    T Scalar<T>(string sql)
    {
        using var c = f.Db.Open();
        return c.ExecuteScalar<T>(sql)!;
    }

    static readonly A.Criterion Good = new(4, "Bien");

    /// <summary>F2.21: la ruta editada en Revisar se aprueba con el nuevo orden, sin pasos inventados; el contador baja.</summary>
    [Fact]
    public void AnEditedRouteIsApprovedAsItWasLeft()
    {
        var id = Item(A.Routes, "saga", 1, new List<A.Route> { new("release", "Por fecha", [new(10, null, "Primero"), new(11, null, "Después")], []) });
        Assert.Equal(1, ClaudeReview.PendingCount(f.Db));

        Assert.True(ClaudeReview.Approve(f.Db, id, [new("release", "x", [new(11, null, "y"), new(999, null, "inventado"), new(10, null, "z")], [])]));

        Assert.Equal(0, ClaudeReview.PendingCount(f.Db));
        var steps = Assert.Single(ClaudeReview.Routes(f.Db, 1).Routes).Steps;
        Assert.Equal([11L, 10L], steps.Select(st => st.GameId));
        Assert.Equal("Después", steps[0].Reason); // el texto es el de Claude: editar solo ordena o quita
    }

    [Fact]
    public void ApprovingAppliesEachResult()
    {
        var routes = Item(A.Routes, "saga", 1, new List<A.Route>
        {
            new("release", "Por fecha", [new(10, null, "Primero"), new(11, null, "Después")], [new("Wiki", "https://zeldawiki.wiki")]),
            new("community", "Así empieza la gente", [new(10, 101, "El de 3DS se ve mejor"), new(11, 999, "Versión inventada")], []),
        });
        var iconic = Item(A.Iconic, "saga", 1, new A.SagaIconic(1, true, [10], "Cambió el 3D", []));
        var best = Item(A.BestVersion, "game", 10, new A.GameBest(10, 101, "Mejor hoy", [new(100, Good, Good, Good, new(5, "Es el original")), new(101, Good, Good, Good, Good)]));
        var kind = Item(A.Classify, "version", 101, new { kind = "remake", reason = "Gráficos nuevos", before = "original" });
        var text = Item(A.Translate, "game", 10, new { text = "Link despierta." });
        var rejected = Item(A.Translate, "game", 11, new { text = "Mal traducido." });

        Assert.Equal(6, ClaudeReview.Page(f.Db).Pending.Count);
        foreach (var id in new[] { routes, iconic, best, kind, text }) Assert.True(ClaudeReview.Approve(f.Db, id));
        Assert.True(ClaudeReview.Reject(f.Db, rejected));
        Assert.False(ClaudeReview.Approve(f.Db, rejected));
        Assert.Empty(ClaudeReview.Page(f.Db).Pending);

        var page = ClaudeReview.Routes(f.Db, 1);
        Assert.Equal(["release", "community"], page.Routes.Select(r => r.Kind));
        var community = page.Routes[1];
        Assert.Equal(community.Id, page.ChosenRoute); // sin ruta elegida, se sigue la de la comunidad
        Assert.Equal(("3ds", (string?)null), (community.Steps[0].VersionPlatform, community.Steps[1].VersionPlatform)); // la 999 no existe
        Assert.Equal(1, Scalar<long>("SELECT iconic FROM saga WHERE id = 1"));
        Assert.Equal(10, Scalar<long>("SELECT group_concat(id) FROM game WHERE iconic")); // sus juegos icónicos también (#112)
        var compare = ClaudeReview.Compare(f.Db, 10);
        Assert.True(compare.Single(v => v.VersionId == 101).Best);
        Assert.Equal("5", compare.Single(v => v.VersionId == 100).Criteria.Single(k => k.Criterion == "original").Value);
        Assert.Equal("remake", Scalar<string>("SELECT kind FROM version WHERE id = 101"));
        Assert.Equal("Link despierta.", Scalar<string>("SELECT value FROM field_value WHERE entity_id = 10 AND field = 'summary_es'"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM field_value WHERE entity_id = 11 AND field = 'summary_es'"));
    }

    /// <summary>#178: la imagen de la saga sale del icónico más reciente con héroe; sin icónicos, del más reciente con héroe.</summary>
    [Fact]
    public void SagaHeroComesFromTheLatestIconicGame()
    {
        Assert.Null(Catalog.CatalogQueries.Saga(f.Db, 1)!.HeroGameId); // sin héroes, el fondo de siempre
        var folder = Catalog.SteamGridDb.Folder(f.Db)!;
        Directory.CreateDirectory(folder);
        foreach (var id in new[] { 10, 11 }) File.WriteAllText(Path.Combine(folder, $"{id}-hero.webp"), "héroe");
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddSeconds(1)); // lo recordado se vuelve a leer
        Assert.Equal(11, Catalog.CatalogQueries.Saga(f.Db, 1)!.HeroGameId); // el más reciente

        Assert.True(ClaudeReview.Approve(f.Db, Item(A.Iconic, "saga", 1, new A.SagaIconic(1, true, [10], "Cambió el 3D", []))));
        Assert.Equal(10, Catalog.CatalogQueries.Saga(f.Db, 1)!.HeroGameId); // el icónico, aunque sea más viejo
    }

    [Fact]
    public void NextGameFollowsTheChosenRoute()
    {
        Assert.True(ClaudeReview.Approve(f.Db, Item(A.Routes, "saga", 1, new List<A.Route>
        {
            new("release", "Por fecha", [new(10, null, "Primero"), new(11, null, "Después")], []),
        })));
        var release = ClaudeReview.Routes(f.Db, 1).Routes.Single();
        Assert.Empty(ClaudeReview.Next(f.Db)); // no la sigues todavía

        Assert.True(ClaudeReview.Choose(f.Db, 1, release.Id)); // elegirla es seguir la saga
        Assert.False(ClaudeReview.Choose(f.Db, 1, 999));
        Assert.Equal(10, Assert.Single(ClaudeReview.Next(f.Db)).Step.GameId);

        using (var c = f.Db.Open()) c.Execute("INSERT INTO progress (game_id, status) VALUES (10, 'finished')");
        var next = Assert.Single(ClaudeReview.Next(f.Db));
        Assert.Equal((11L, 1, 2), (next.Step.GameId, next.Done, next.Total));
    }

    [Fact]
    public void ClaudeStatusShowsSettingsAndUsage()
    {
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO claude_usage (prompt, model, duration_ms, result, input_tokens, output_tokens, cost_usd, created_at)
                VALUES ('ruta-saga', 'sonnet', 1200, 'ok', 100, 10, 0, '2026-10-31T03:00:00Z')
                """);
        Assert.Equal("claude.badTime", ClaudeBudget.Save(f.Db, new("25:00", null, null))?.Code);
        Assert.Equal("claude.badShare", ClaudeBudget.Save(f.Db, new(null, null, 5))?.Code);
        Assert.Null(ClaudeBudget.Save(f.Db, new("23:00", "07:00", 40)));

        var status = ClaudeBudget.Status(f.Db);
        Assert.Equal(("23:00", "07:00", 40), (status.ScheduleStart, status.ScheduleEnd, status.WindowShare));
        var usage = ClaudeBudget.Usage(f.Db, 1);
        Assert.Equal(("ruta-saga", 100L), (Assert.Single(usage.Rows).Prompt, usage.Rows[0].InputTokens));
    }

    /// <summary>#95: el registro de uso va de a 10, de la llamada más nueva a la más vieja; una página fuera de rango da la última.</summary>
    [Fact]
    public void UsageLogIsPaged()
    {
        var empty = ClaudeBudget.Usage(f.Db, 1);
        Assert.Equal((0, 1, 1), (empty.Rows.Count, empty.Page, empty.Pages));
        using (var c = f.Db.Open())
            for (var i = 1; i <= 12; i++)
                c.Execute("""
                    INSERT INTO claude_usage (prompt, model, duration_ms, result, input_tokens, output_tokens, cost_usd, created_at)
                    VALUES (@prompt, 'haiku', 1000, 'ok', 1, 1, 0, '2026-10-31T03:00:00Z')
                    """, new { prompt = $"p{i}" });

        var first = ClaudeBudget.Usage(f.Db, 1);
        Assert.Equal((10, 1, 2, "p12"), (first.Rows.Count, first.Page, first.Pages, first.Rows[0].Prompt));
        var last = ClaudeBudget.Usage(f.Db, 9);
        Assert.Equal((2, "p1"), (last.Page, last.Rows[^1].Prompt));
    }
}

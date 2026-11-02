using Dapper;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#17: editar sagas, rutas y juegos a mano, y que IGDB y Claude no lo pisen.</summary>
public class EditingTests
{
    static void Fill(Fixture f)
    {
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO catalog.saga (id, name) VALUES (33, 'Final Fantasy'), (40, 'Zelda');
            INSERT INTO catalog.game (id, saga_id, name_es, year) VALUES (10, 33, 'Final Fantasy X', 2001), (11, 33, 'Final Fantasy X HD', 2013), (12, NULL, 'Tetris', 1984);
            INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (20, 10, 'ps2', 'original'), (21, 11, 'ps4', 'remaster'), (22, 12, 'gb', 'original');
            """);
    }

    static RouteEdit Route(params (long Game, long? Version)[] steps) => new("Por orden de salida", steps.Select(s => new RouteStepEdit(s.Game, s.Version, null)).ToList());

    [Fact]
    public async Task ARouteIsSavedInTheGivenOrderAndKeepsItsIdWhenEditedAgain()
    {
        await using var f = new Fixture();
        Fill(f);
        Assert.Null(Editing.SaveRoute(f.Db, 33, "release", Route((11, 21), (10, null))));
        var first = Claude.ClaudeReview.Routes(f.Db, 33).Routes.Single();
        Assert.Equal([11L, 10L], first.Steps.Select(s => s.GameId));

        Assert.Null(Editing.SaveRoute(f.Db, 33, "release", Route((10, 20), (11, null))));
        var again = Claude.ClaudeReview.Routes(f.Db, 33).Routes.Single();
        Assert.Equal(first.Id, again.Id);
        Assert.Equal([(10L, (long?)20L), (11L, null)], again.Steps.Select(s => (s.GameId, s.VersionId)));
    }

    [Fact]
    public async Task AStepOnlyTakesAGameAndAVersionThatExist()
    {
        await using var f = new Fixture();
        Fill(f);
        Assert.Equal("edit.noGame", Editing.SaveRoute(f.Db, 33, "story", Route((999, null)))!.Code);
        Assert.Equal("edit.badVersion", Editing.SaveRoute(f.Db, 33, "story", Route((10, 21)))!.Code); // 21 es de otro juego
        Assert.Equal("edit.routeKind", Editing.SaveRoute(f.Db, 33, "otra", Route((10, null)))!.Code);
        Assert.Equal("edit.emptyRoute", Editing.SaveRoute(f.Db, 33, "story", new RouteEdit(null, []))!.Code);
        Assert.Empty(Claude.ClaudeReview.Routes(f.Db, 33).Routes);
    }

    [Fact]
    public async Task MergingAGameMovesItsVersionsAndStepsAndIsPublishedAsReplaced()
    {
        await using var f = new Fixture();
        Fill(f);
        Assert.Null(Editing.SaveRoute(f.Db, 33, "release", Route((11, 21))));
        using (var c = f.Db.Open()) c.Execute("INSERT INTO catalog.replaced (kind, old_id, new_id) VALUES ('game', 5, 11)");

        Assert.Null(Editing.MergeGame(f.Db, 11, 10));

        using (var c = f.Db.Open())
        {
            Assert.Equal(10, c.ExecuteScalar<long>("SELECT game_id FROM version WHERE id = 21"));
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE id = 11"));
            Assert.Equal([(5L, 10L), (11L, 10L)], c.Query<(long, long)>("SELECT old_id, new_id FROM replaced WHERE kind = 'game' ORDER BY old_id"));
        }
        Assert.Equal(10, Claude.ClaudeReview.Routes(f.Db, 33).Routes.Single().Steps.Single().GameId);
        Assert.Equal("edit.sameGame", Editing.MergeGame(f.Db, 10, 10)!.Code);

        var folder = f.Tmp["publicado"];
        Published.Write(f.Db, folder);
        Assert.Empty(Published.Validate(folder));
    }

    [Fact]
    public async Task DeletingAGameRecordsItAndItsVersionsWithoutReplacement()
    {
        await using var f = new Fixture();
        Fill(f);
        Assert.Null(Editing.DeleteGame(f.Db, 12));
        using var c = f.Db.Open();
        Assert.Equal([("game", 12L), ("version", 22L)], c.Query<(string, long)>("SELECT kind, old_id FROM replaced WHERE new_id IS NULL ORDER BY kind"));
        var folder = f.Tmp["publicado"];
        Published.Write(f.Db, folder);
        Assert.Empty(Published.Validate(folder));
    }

    [Fact]
    public async Task ANewSagaGetsTheNextIdAndItsNameIsManual()
    {
        await using var f = new Fixture();
        Fill(f);
        var (id, error) = Editing.CreateSaga(f.Db, "  Mana  ");
        Assert.Null(error);
        Assert.Equal(41, id);
        Assert.Equal("Mana", CatalogQueries.Saga(f.Db, 41)!.Saga.Name);
        Assert.Equal("fields.empty", Editing.CreateSaga(f.Db, " ").Error!.Code);

        Assert.Null(Editing.MoveGame(f.Db, 12, 41));
        Assert.Equal(41, CatalogQueries.Game(f.Db, 12)!.SagaId);
    }

    [Fact]
    public async Task ClaudeDoesNotReplaceARouteOrIconicEditedByHand()
    {
        await using var f = new Fixture();
        Fill(f);
        Assert.Null(Editing.SaveRoute(f.Db, 33, "community", Route((10, null))));
        Assert.Null(Editing.SetIconic(f.Db, 33, false));
        using (var c = f.Db.Open())
        {
            // Lo que Claude propuso y quedó por aprobar en Revisar.
            foreach (var data in new[]
            {
                """{ "prompt": "ruta-saga", "result": [ { "kind": "community", "rationale": "Claude", "sources": [], "steps": [ { "gameId": 11 } ] } ] }""",
                """{ "prompt": "iconico", "result": { "sagaId": 33, "iconic": true, "gameIds": [10], "reason": "x", "sources": [] } }""",
            })
                c.Execute("INSERT INTO review_item (kind, entity, entity_id, data, created_at) VALUES ('pending_approval', 'saga', 33, @data, '2026-01-01')", new { data });
            foreach (var id in c.Query<long>("SELECT id FROM review_item ORDER BY id")) Assert.True(Claude.ClaudeReview.Approve(f.Db, id));
            Assert.False(c.ExecuteScalar<bool>("SELECT iconic FROM saga WHERE id = 33"));
        }
        Assert.Equal(10, Claude.ClaudeReview.Routes(f.Db, 33).Routes.Single().Steps.Single().GameId);
    }

    [Fact]
    public async Task IgdbDoesNotPutBackAGameTakenOutOfItsSagaByHand()
    {
        await using var f = new Fixture();
        Fill(f);
        Assert.Null(Editing.MoveGame(f.Db, 10, null));
        using var c = f.Db.Open();
        // Lo que hace la carga de IGDB con un juego sin saga.
        c.Execute($"UPDATE game SET saga_id = 40 WHERE id = 10 AND {Editing.NotMovedByHand}");
        Assert.Null(c.ExecuteScalar<long?>("SELECT saga_id FROM game WHERE id = 10"));
    }
}

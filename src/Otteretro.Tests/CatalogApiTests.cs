using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>F1.9: lo que leen y guardan las pantallas del catálogo, con un catálogo pequeño de ejemplo.</summary>
public class CatalogApiTests : IAsyncLifetime
{
    readonly Fixture f = new();
    WebApplication app = null!;
    HttpClient http = null!;

    public async Task InitializeAsync()
    {
        using (var c = f.Db.Open())
        {
            c.Execute("""
                INSERT INTO saga (id, name, iconic) VALUES (1, 'The Legend of Zelda', 1), (2, 'Half-Life', 0);
                INSERT INTO saga_user (saga_id, favorite) VALUES (1, 1);
                INSERT INTO game (id, saga_id, name_es, original_name, year) VALUES
                    (10, 1, 'Ocarina of Time', 'Ocarina of Time', 1998), (11, 1, 'Breath of the Wild', 'Breath of the Wild', 2017),
                    (20, 2, 'Half-Life', 'Half-Life', 1998), (30, NULL, 'Tetris', 'Tetris', 1989);
                INSERT INTO version (id, game_id, platform, kind, year, steam_app_id) VALUES
                    (100, 10, 'n64', 'original', 1998, NULL), (101, 10, '3ds', 'remake', 2011, NULL), (110, 11, 'switch', 'original', 2017, NULL),
                    (200, 20, 'pc', 'original', 1998, 70), (300, 30, 'gb', 'original', 1989, NULL);
                INSERT INTO source (kind, store, store_id, name, playtime, last_played, version_id) VALUES
                    ('store', 'steam', '70', 'Half-Life', 600, '2026-10-01T00:00:00Z', 200),
                    ('store', 'steam', '999', 'Algo que IGDB no tiene', 5, NULL, NULL);
                """);
            FieldValues.Set(c, "game", 11, "name_es", "Breath of the Wild", "igdb");
        }
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

    Task<T?> Get<T>(string url) => http.GetFromJsonAsync<T>(url);

    [Fact]
    public async Task SagasHaveTheirPlatformsFiltersAndEmulationLast()
    {
        var all = (await Get<SagaPage>("/api/sagas"))!.Items;
        Assert.Equal([2L, 1L, 0L], all.Select(s => s.Id)); // por nombre; «Emulación» al final
        var zelda = all.Single(s => s.Id == 1);
        Assert.Equal(["n64", "3ds", "switch"], zelda.Platforms);
        Assert.Equal((2, 0), (zelda.Games, zelda.Owned));

        Assert.Equal([2L], (await Get<SagaPage>("/api/sagas?filter=pc"))!.Items.Select(s => s.Id));
        Assert.Equal([1L], (await Get<SagaPage>("/api/sagas?filter=favorite"))!.Items.Select(s => s.Id));
        Assert.Equal([2L], (await Get<SagaPage>("/api/sagas?filter=owned"))!.Items.Select(s => s.Id));
        Assert.Equal([1L, 0L], (await Get<SagaPage>("/api/sagas?company=Nintendo"))!.Items.Select(s => s.Id));
        Assert.Equal(2L, (await Get<SagaPage>("/api/sagas?sort=played"))!.Items[0].Id);
        Assert.Equal(1L, (await Get<SagaPage>("/api/sagas?sort=recognized"))!.Items[0].Id);

        Assert.Equal(["Tetris"], (await Get<SagaDetail>("/api/sagas/0"))!.Games.Select(g => g.Name));
    }

    /// <summary>#177: Sagas, los juegos de una saga y Colección se piden por tramos, con el total para seguir bajando.</summary>
    [Fact]
    public async Task ListsComeInPagesWithTheirTotal()
    {
        var first = (await Get<SagaPage>("/api/sagas?limit=2"))!;
        Assert.Equal((2, 3), (first.Items.Count, first.Total));
        var rest = (await Get<SagaPage>("/api/sagas?offset=2&limit=2"))!;
        Assert.Equal([0L], rest.Items.Select(s => s.Id)); // «Emulación», al final
        Assert.Equal(10L, first.Items.Single(s => s.Id == 1).CoverGameId ?? 10L); // las del tramo llevan su portada (aquí, sin archivos)

        var zelda = (await Get<GamePage>("/api/sagas/1/games?offset=1&limit=1"))!;
        Assert.Equal((2, "Breath of the Wild"), (zelda.Total, Assert.Single(zelda.Items).Name));

    }

    /// <summary>F2.16 y F2.17: exclusivas de una empresa (PC no cuenta) y «más reconocidas» (icónico, después popularidad en IGDB).</summary>
    [Fact]
    public async Task ExclusivesAndMostRecognized()
    {
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO saga (id, name) VALUES (3, 'Persona'), (4, 'Halo');
                INSERT INTO game (id, saga_id, name_es, popularity) VALUES (40, 3, 'Persona 5', 50), (50, 4, 'Halo', 2000);
                INSERT INTO version (game_id, platform) VALUES (40, 'ps2'), (40, 'switch'), (50, 'xbox'), (50, 'pc');
                UPDATE game SET popularity = 900 WHERE id = 20;
                """);

        Assert.Equal([4L, 1L], (await Get<SagaPage>("/api/sagas?filter=exclusive"))!.Items.Select(s => s.Id)); // Persona no; Half-Life es solo PC
        Assert.Equal([1L], (await Get<SagaPage>("/api/sagas?filter=exclusive&company=Nintendo"))!.Items.Select(s => s.Id));
        Assert.Equal("Microsoft", (await Get<SagaDetail>("/api/sagas/4"))!.Saga.Exclusive);
        Assert.Equal([1L, 4L, 2L, 3L, 0L], (await Get<SagaPage>("/api/sagas?sort=recognized"))!.Items.Select(s => s.Id));
        Assert.Equal([1L, 3L], (await Get<SagaPage>("/api/sagas?sort=recognized&platform=switch"))!.Items.Select(s => s.Id));
    }

    [Fact]
    public async Task GameSheetSavesManualFixes()
    {
        var game = (await Get<GameDetail>("/api/games/10"))!;
        Assert.Equal(["n64", "3ds"], game.Versions.Select(v => v.Platform));
        Assert.Equal("pending", game.Progress.Status);


        // A mano gana sobre IGDB; al quitarlo, vuelve el de IGDB.
        var fixedName = await (await http.PutAsJsonAsync("/api/games/11/fields/name_es", new FieldRequest("Zelda: Aliento de lo salvaje"))).Content.ReadFromJsonAsync<GameDetail>();
        Assert.Equal("Zelda: Aliento de lo salvaje", fixedName!.NameEs);
        Assert.Equal(("manual", true), fixedName.Fields.Where(x => x.Winner).Select(x => (x.Source, x.Manual)).Single());
        Assert.Equal("Breath of the Wild", (await (await http.DeleteAsync("/api/games/11/fields/name_es")).Content.ReadFromJsonAsync<GameDetail>())!.NameEs);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/games/11/fields/year", new FieldRequest("hace mucho"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/games/11/fields/name_es", new FieldRequest(null))).StatusCode); // #59
        Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsJsonAsync("/api/games/11/fields/saga_id", new FieldRequest("2"))).StatusCode);
    }

    /// <summary>#132: el recomendado de una consola, elegido a mano en la ficha, reemplaza al anterior.</summary>
    [Fact]
    public async Task GameSheetChoosesTheRecommendedEmulator()
    {
        using (var c = f.Db.Open())
            foreach (var id in new[] { "mupen64plus", "rmg" }) // sin «asset»: guardar no consulta releases
                c.Execute("INSERT INTO recipe (id, kind, json) VALUES (@id, 'emulator', @json)",
                    new { id, json = $$"""{ "id": "{{id}}", "name": "{{id}}", "kind": "emulator", "folder": "N64", "detect": "x.exe", "consoles": ["n64"] }""" });

        Assert.Equal(new BestEmulatorState(null, null), await Get<BestEmulatorState>("/api/games/10/best-emulator"));
        var first = await (await http.PutAsJsonAsync("/api/games/10/recommended/n64", new RecommendRequest("mupen64plus", "https://example.org"))).Content.ReadFromJsonAsync<List<GameRecipe>>();
        Assert.Equal(("mupen64plus", "https://example.org"), first!.Select(r => (r.Id, r.Source)).Single());
        var second = await (await http.PutAsJsonAsync("/api/games/10/recommended/n64", new RecommendRequest("rmg"))).Content.ReadFromJsonAsync<List<GameRecipe>>();
        Assert.Equal(("rmg", null), second!.Select(r => (r.Id, r.Source)).Single());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/games/10/recommended/3ds", new RecommendRequest("rmg"))).StatusCode);
        Assert.Empty((await (await http.DeleteAsync("/api/games/10/recommended/n64")).Content.ReadFromJsonAsync<List<GameRecipe>>())!);
    }

    [Fact]
    public async Task SearchFindsSagasAndGamesAndNoticesListMissingKeys()
    {
        var results = (await Get<SearchResults>("/api/search?q=zelda"))!;
        Assert.Equal(["The Legend of Zelda"], results.Sagas.Select(s => s.Name));
        Assert.Equal(["Half-Life"], (await Get<SearchResults>("/api/search?q=half"))!.Games.Select(g => g.Name));
        Assert.Empty((await Get<SearchResults>("/api/search?q=z"))!.Games);

        var notices = (await Get<List<ApiError>>("/api/notices"))!;
        Assert.Equal(["igdb", "steamgriddb"], notices.Select(n => n.Params!["service"])); // #33: Steam es de Otteretro
    }
}

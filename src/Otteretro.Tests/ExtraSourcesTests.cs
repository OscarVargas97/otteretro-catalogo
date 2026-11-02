using System.Net;
using Dapper;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>F1.6: Wikidata y RAWG completan a IGDB, con la prioridad por campo y lo manual siempre primero.</summary>
[Collection("FakeHttp")]
public class ExtraSourcesTests
{
    sealed class FakeSources : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var json = url switch
            {
                Igdb.TokenUrl => """{ "access_token": "token", "expires_in": 3600 }""",
                _ when url.StartsWith(Igdb.ApiUrl) =>
                    """[ { "id": 1027, "slug": "the-legend-of-zelda-ocarina-of-time" }, { "id": 231, "slug": "half-life" }, { "id": 71, "slug": "portal" } ]""",
                _ when url.StartsWith(ExtraSources.WikidataUrl) => """
                    { "results": { "bindings": [
                      { "slug": { "value": "the-legend-of-zelda-ocarina-of-time" }, "seriesLabel": { "value": "the legend of zelda" } },
                      { "slug": { "value": "half-life" }, "seriesLabel": { "value": "Q752241" } },
                      { "slug": { "value": "portal" }, "seriesLabel": { "value": "Portal" } } ] } }
                    """,
                _ when url.StartsWith(ExtraSources.RawgUrl + "?search=Portal&") =>
                    """{ "results": [ { "name": "Portal 2", "released": "2011-04-18" }, { "name": "Portal", "released": "2007-10-09" } ] }""",
                _ when url.StartsWith(ExtraSources.RawgUrl) => """{ "results": [ { "name": "Otro juego", "released": "1990-01-01" } ] }""",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    public ExtraSourcesTests() => ExtraSources.Handler = new FakeSources();

    [Fact]
    public async Task WikidataLinksSagasAndRawgFillsTheMissingYear()
    {
        await using var f = new Fixture(("igdb.clientId", "id"), ("igdb.clientSecret", "secreto"), ("rawg.apiKey", "clave-rawg"));
        using (var c = f.Db.Open())
        {
            c.Execute("""
                INSERT INTO saga (id, name) VALUES (5, 'The Legend of Zelda');
                INSERT INTO game (id, name_es, original_name, year, igdb_id, saga_id) VALUES
                    (1, 'Ocarina of Time', 'Ocarina of Time', 1998, 1027, NULL), (2, 'Half-Life', 'Half-Life', 1998, 231, NULL),
                    (3, 'Portal', 'Portal', NULL, 71, NULL), (4, 'Raro', 'Raro', NULL, NULL, 5);
                """);
            FieldValues.Set(c, "game", 3, "year", null, "igdb"); // IGDB no trae el año
        }

        var task = await f.Run(ExtraSources.Enqueue(f.Db));
        Assert.Equal(("done", 100), (task.Status, task.Progress));

        using var db = f.Db.Open();
        var games = db.Query<(long Id, long? SagaId, long? Year)>("SELECT id, saga_id, year FROM game ORDER BY id").ToList();
        var portal = db.ExecuteScalar<long>("SELECT id FROM saga WHERE name = 'Portal'");
        // Zelda va a la saga que ya existía (sin importar mayúsculas); Half-Life no (la serie no tiene nombre); Portal crea la suya.
        Assert.Equal([(1L, 5L, 1998L), (2L, null, 1998L), (3L, portal, 2007L), (4L, 5L, null)], games.Select(g => (g.Id, g.SagaId, g.Year)));
        // La clave de RAWG nunca queda en la caché.
        Assert.DoesNotContain(db.Query<string>("SELECT key FROM api_cache"), k => k.Contains("clave-rawg"));
    }

    [Fact]
    public async Task WithoutKeysTheStepsAreSkipped()
    {
        await using var f = new Fixture();
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO game (id, name_es, igdb_id) VALUES (3, 'Portal', 71)");
        Assert.Equal("done", (await f.Run(ExtraSources.Enqueue(f.Db))).Status);
        using var db = f.Db.Open();
        Assert.Null(db.ExecuteScalar<long?>("SELECT saga_id FROM game WHERE id = 3"));
    }

    /// <summary>Listo cuando de F1.6: la prioridad por campo y que lo manual gana.</summary>
    [Fact]
    public async Task PriorityByFieldAndManualAlwaysWins()
    {
        await using var f = new Fixture();
        using var c = f.Db.Open();
        c.Execute("INSERT INTO game (id, name_es) VALUES (1, 'Juego')");
        long? Year() => c.ExecuteScalar<long?>("SELECT year FROM game WHERE id = 1");

        FieldValues.Set(c, "game", 1, "year", null, "igdb");
        FieldValues.Set(c, "game", 1, "year", 1999, "rawg");
        Assert.Equal(1999, Year()); // IGDB sin dato: gana RAWG
        FieldValues.Set(c, "game", 1, "year", 1998, "igdb");
        Assert.Equal(1998, Year()); // con dato, IGDB va antes que RAWG
        FieldValues.Set(c, "game", 1, "year", 2000, FieldValues.Manual);
        FieldValues.Set(c, "game", 1, "year", 1997, "igdb");
        FieldValues.Set(c, "game", 1, "year", 1996, "rawg");
        Assert.Equal(2000, Year()); // ninguna carga pisa lo manual
        FieldValues.Clear(c, "game", 1, "year", FieldValues.Manual);
        Assert.Equal(1997, Year());
    }
}

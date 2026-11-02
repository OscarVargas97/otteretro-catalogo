using System.Net;
using System.Text.Json;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Lo que completa a IGDB (F1.6), en la tarea `catalog.extra` que encola cada carga:
/// Wikidata pone en su saga los juegos que IGDB dejó sin ella, y RAWG trae el año que IGDB no tiene.
/// Son complementos: sin la clave de RAWG ese paso se salta, y lo que no encuentran queda como estaba.
/// </summary>
public static class ExtraSources
{
    public const string WikidataUrl = "https://query.wikidata.org/sparql";
    public const string RawgUrl = "https://api.rawg.io/api/games";
    const string Source = "rawg";
    const int Chunk = 50;

    /// <summary>Wikidata y RAWG cambian poco: un mes en caché.</summary>
    static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    /// <summary>Para las pruebas: el manejador HTTP falso de IGDB, Wikidata y RAWG.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    public record Job(long After = 0);
    record Pending(long Id, long? IgdbId, long? SagaId, string Name, long? Year);
    record RawgGame(string Name, string? Released);
    record RawgPage(RawgGame[] Results);

    public static long Enqueue(Database db) => TaskQueue.Enqueue(db, "catalog.extra", JsonSerializer.Serialize(new Job(), JsonSerializerOptions.Web));

    public static async Task Run(TaskRun run, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<Job>(run.Data!, JsonSerializerOptions.Web)!;
        var api = new ApiClient(run.Db, Handler);
        var rawgKey = run.Credentials.Read("rawg.apiKey");
        var hasIgdb = !string.IsNullOrEmpty(run.Credentials.Read("igdb.clientId")) && !string.IsNullOrEmpty(run.Credentials.Read("igdb.clientSecret"));
        var igdb = new Igdb(api, run.Credentials, Handler);

        List<Pending> games;
        using (var c = run.Db.Open())
            games = c.Query<Pending>("""
                SELECT id, igdb_id AS IgdbId, saga_id AS SagaId, coalesce(original_name, name_es) AS Name, year
                FROM game WHERE id > @After AND ((saga_id IS NULL AND igdb_id IS NOT NULL) OR year IS NULL) ORDER BY id
                """, job).ToList();

        for (var i = 0; i < games.Count; i += Chunk)
        {
            var chunk = games.Skip(i).Take(Chunk).ToList();
            var noSaga = chunk.Where(g => g.SagaId is null && g.IgdbId is not null).ToList();
            if (hasIgdb && noSaga.Count > 0) await LinkSagas(run.Db, api, igdb, noSaga, ct);
            if (!string.IsNullOrEmpty(rawgKey))
                foreach (var g in chunk.Where(g => g.Year is null))
                    if (await RawgYear(api, rawgKey, g.Name, ct) is { } year)
                        using (var c = run.Db.Open())
                            FieldValues.Set(c, "game", g.Id, "year", year, Source);

            var done = Math.Min(i + Chunk, games.Count);
            run.Report(done * 100 / games.Count, ApiError.Of("tasks.extra.progress", ("done", done.ToString()), ("total", games.Count.ToString())),
                JsonSerializer.Serialize(new Job(chunk[^1].Id), JsonSerializerOptions.Web));
        }
    }

    /// <summary>
    /// La saga según Wikidata («parte de la serie», P179), buscando cada juego por su ficha de IGDB (P5794, el slug).
    /// Si ya hay una saga con ese nombre se usa esa; si no, se crea.
    /// </summary>
    static async Task LinkSagas(Database db, ApiClient api, Igdb igdb, List<Pending> games, CancellationToken ct)
    {
        var slugs = (await igdb.Query<IgdbNamed>("games", $"fields slug; where id = ({string.Join(",", games.Select(g => g.IgdbId))}); limit 500;", ct))
            .Where(n => n.Slug is not null).ToDictionary(n => n.Id, n => n.Slug!);
        if (slugs.Count == 0) return;

        var values = string.Join(" ", slugs.Values.Select(s => JsonSerializer.Serialize(s)));
        var query = $$"""SELECT ?slug ?seriesLabel WHERE { VALUES ?slug { {{values}} } ?item wdt:P5794 ?slug; wdt:P179 ?series. SERVICE wikibase:label { bd:serviceParam wikibase:language "en". } }""";
        string text;
        try
        {
            text = await api.Send("wikidata", HttpMethod.Get, $"{WikidataUrl}?format=json&query={Uri.EscapeDataString(query)}", null, Ttl,
                r => r.Headers.UserAgent.ParseAdd("Otteretro/1.0 (https://github.com/OscarVargas97/otteretro)"), ct);
        }
        catch (HttpRequestException e)
        {
            // Es un complemento: si Wikidata no responde, esos juegos siguen en «Emulación» hasta la próxima carga.
            FileLog.Write(Microsoft.Extensions.Logging.LogLevel.Warning, "Wikidata", e.Message);
            return;
        }

        // Una serie sin nombre en inglés llega como su id (Q752241): no sirve como nombre de saga.
        var series = JsonDocument.Parse(text).RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray()
            .Select(b => (Slug: b.GetProperty("slug").GetProperty("value").GetString()!, Name: b.GetProperty("seriesLabel").GetProperty("value").GetString()!))
            .Where(s => !System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"^Q\d+$"))
            .GroupBy(s => s.Slug).ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToList());

        using var c = db.Open();
        using var t = c.BeginTransaction();
        var sagas = c.Query<(long Id, string Name)>("SELECT id, name FROM saga", transaction: t).ToList();
        foreach (var g in games)
        {
            if (!slugs.TryGetValue(g.IgdbId!.Value, out var slug) || !series.TryGetValue(slug, out var names)) continue;
            // Si el juego está en varias series, gana la que ya es una saga del catálogo.
            var existing = names.Select(n => sagas.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(s => s.Id != 0);
            var sagaId = existing.Id != 0 ? existing.Id : c.ExecuteScalar<long>("INSERT INTO saga (name) VALUES (@name) RETURNING id", new { name = names[0] }, t);
            if (existing.Id == 0) sagas.Add((sagaId, names[0]));
            c.Execute("UPDATE game SET saga_id = @sagaId WHERE id = @Id AND saga_id IS NULL", new { sagaId, g.Id }, t);
        }
        t.Commit();
    }

    /// <summary>El año de RAWG, solo si el nombre coincide exacto (sin mayúsculas): un año equivocado es peor que ninguno.</summary>
    static async Task<int?> RawgYear(ApiClient api, string key, string name, CancellationToken ct)
    {
        var url = $"{RawgUrl}?search={Uri.EscapeDataString(name)}&page_size=5";
        string text;
        try
        {
            // La clave va en la URL, pero se agrega al enviar: no queda en la clave de la caché.
            text = await api.Send("rawg", HttpMethod.Get, url, null, Ttl, r => r.RequestUri = new Uri($"{url}&key={Uri.EscapeDataString(key)}"), ct);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TaskException(ApiError.Of("tasks.extra.rawgBadKey"));
        }
        catch (HttpRequestException)
        {
            return null; // RAWG caído o sin ese juego: el año queda como estaba
        }
        var match = JsonSerializer.Deserialize<RawgPage>(text, JsonSerializerOptions.Web)?.Results
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && r.Released is { Length: >= 4 });
        return match is null ? null : int.Parse(match.Released![..4]);
    }
}

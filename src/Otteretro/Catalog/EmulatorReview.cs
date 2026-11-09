using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Catalog;

public record ForkGame(string Game, string? Source);
/// <param name="Recipe">El id de la receta original.</param>
/// <param name="FoundBy">Cómo se encontró (forks, búsqueda o lista:&lt;nombre&gt;).</param>
/// <param name="Reason">Lo que dijo Claude, si hubo que preguntarle.</param>
/// <param name="Tag">La release que pasó los filtros, con su archivo de Windows.</param>
/// <param name="RecommendedFor">Los juegos para los que se recomienda; puede venir vacío (forks del repo de la receta).</param>
public record ForkCandidate(long Id, string Repo, string Url, string Recipe, string? RecipeName, string FoundBy, string? Reason, int Stars, string Tag,
    string? PublishedAt, string File, bool IsFork, List<ForkGame> RecommendedFor);
/// <param name="Reason">gone, moved, archived, noRelease o noAsset.</param>
/// <param name="Proposed">noAsset: el patrón que saldría del único archivo de Windows de la release.</param>
public record RecipeNotice(long Id, string Recipe, string? RecipeName, string Reason, string Repo, string? To, string? Tag, string? Pattern, string? Proposed);

/// <summary>Lo que la tarea «Emuladores» (#97) deja en Revisar y se resuelve ahí (#98): candidatos de fork y avisos de recetas.</summary>
public static class EmulatorReview
{
    /// <summary>El juego del catálogo que se llama así (nombre en español u original, sin distinguir mayúsculas), si es uno solo (#121).</summary>
    // ponytail: igualdad exacta del nombre; si los nombres de las fuentes no calzan, el gameId se elige después en el editor.
    static long? GameByName(System.Data.IDbConnection c, string name) =>
        c.Query<long>("SELECT id FROM game WHERE name_es = @name COLLATE NOCASE OR original_name = @name COLLATE NOCASE LIMIT 2", new { name }).ToList() is [var id] ? id : null;

    static string? Name(Database db, string id) => Recipes.Get(db, id) is { } json ? (string?)JsonNode.Parse(json)!["name"] : null;

    public static List<ForkCandidate> Forks(Database db)
    {
        using var c = db.Open();
        return c.Query<(long Id, string Data)>("SELECT id, data FROM review_item WHERE entity = 'fork' AND status = 'open' ORDER BY id").Select(r =>
        {
            var j = JsonNode.Parse(r.Data)!;
            var recipe = (string)j["recipe"]!;
            return new ForkCandidate(r.Id, (string)j["repo"]!, (string)j["url"]!, recipe, Name(db, recipe), (string?)j["foundBy"] ?? "", (string?)j["reason"],
                (int?)j["passed"]?["stars"] ?? 0, (string?)j["passed"]?["tag"] ?? "", (string?)j["passed"]?["publishedAt"], (string?)j["passed"]?["file"] ?? "",
                (bool?)j["passed"]?["isFork"] ?? false,
                j["recommendedFor"]?.AsArray().Select(g => new ForkGame((string?)g?["game"] ?? "", (string?)g?["source"])).ToList() ?? []);
        }).ToList();
    }

    public static List<RecipeNotice> Notices(Database db)
    {
        using var c = db.Open();
        return c.Query<(long Id, string Data)>("SELECT id, data FROM review_item WHERE entity = 'recipe' AND status = 'open' ORDER BY id").Select(r =>
        {
            var j = JsonNode.Parse(r.Data)!;
            var recipe = (string)j["recipe"]!;
            return new RecipeNotice(r.Id, recipe, Name(db, recipe), (string)j["reason"]!, (string?)j["repo"] ?? "", (string?)j["to"], (string?)j["tag"],
                (string?)j["pattern"], (string?)j["proposed"]);
        }).ToList();
    }

    /// <summary>Descarta un aviso o rechaza un candidato. Un fork rechazado queda visto: no vuelve a salir. False si no existe o ya se resolvió.</summary>
    public static bool Dismiss(Database db, long id)
    {
        using var c = db.Open();
        var data = c.ExecuteScalar<string?>("SELECT data FROM review_item WHERE id = @id AND entity IN ('recipe', 'fork') AND status = 'open'", new { id });
        if (data is null) return false;
        c.Execute("UPDATE review_item SET status = 'rejected' WHERE id = @id", new { id });
        if ((string?)JsonNode.Parse(data)!["url"] is { } url)
            c.Execute("UPDATE fork_seen SET result = 'rejected' WHERE repo = @repo", new { repo = Regex.Replace(url, "^https?://", "").ToLowerInvariant() });
        return true;
    }

    /// <summary>
    /// Acepta un candidato: una receta experimental copia de la original, con su fuente en el fork, <c>forkOf</c> y <c>recommendedFor</c>.
    /// De la copia salen lo que es de la original: sus descargas y ser base de una consola. Se guarda como cualquier receta (esquema y una
    /// release que calce con el patrón). Found es false si el candidato no existe o ya se resolvió.
    /// </summary>
    public static async Task<(bool Found, ApiError? Error)> Accept(Database db, long id, string? token, CancellationToken ct = default)
    {
        string? data;
        using (var c = db.Open())
            data = c.ExecuteScalar<string?>("SELECT data FROM review_item WHERE id = @id AND entity = 'fork' AND status = 'open'", new { id });
        if (data is null) return (false, null);
        var j = JsonNode.Parse(data)!;
        var repo = (string)j["repo"]!;
        var from = (string)j["recipe"]!;
        if (Recipes.Get(db, from) is not { } original) return (true, ApiError.Of("review.noOriginal", ("recipe", from)));
        var r = JsonNode.Parse(original)!.AsObject();

        var slug = Regex.Replace(repo.ToLowerInvariant().Replace('/', '-'), "[^a-z0-9-]", "-");
        var newId = slug;
        for (var n = 2; Recipes.Get(db, newId) is not null; n++) newId = $"{slug}-{n}";
        r["id"] = newId;
        r["name"] = $"{(string?)r["name"] ?? from} ({repo.Split('/')[0]})";
        r["experimental"] = true;
        r["forkOf"] = from;
        r.Remove("downloads");
        r.Remove("baseFor");
        var updates = new JsonObject { ["source"] = (string)j["source"]! };
        if ((string?)j["host"] is { } host) updates["host"] = host;
        updates["repo"] = repo;
        r["updates"] = updates;
        if ((string?)j["passed"]?["file"] is { } file) r["asset"] = Recipes.AssetPattern(file);
        List<JsonObject> games;
        using (var c = db.Open())
            games = (j["recommendedFor"]?.AsArray() ?? []).Where(g => (string?)g?["game"] is { Length: > 0 }).Select(g =>
            {
                var name = (string)g!["game"]!;
                var item = new JsonObject { ["game"] = name };
                if (GameByName(c, name) is { } gameId) item["gameId"] = gameId;
                if ((string?)g["source"] is { } source) item["source"] = source;
                return item;
            }).ToList();
        if (games.Count > 0) r["recommendedFor"] = new JsonArray([.. games]);

        if (await Recipes.Save(db, newId, r.ToJsonString(), true, token, ct) is { } error) return (true, error);
        using (var c = db.Open()) c.Execute("UPDATE review_item SET status = 'approved' WHERE id = @id", new { id });
        return (true, null);
    }
}

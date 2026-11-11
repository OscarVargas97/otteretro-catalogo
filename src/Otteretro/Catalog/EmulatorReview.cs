using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="Recipe">La receta recomendada; si Claude dio un repo, la del catálogo con ese repo y esa consola, o null si todavía no hay.</param>
/// <param name="Repo">El repo que dio Claude cuando no había receta.</param>
public record EmulatorPick(long Id, long GameId, string Game, string Console, string? Recipe, string? RecipeName, string? Repo, string Source, string Reason);
/// <summary>Un aviso en la tarjeta de una receta (#98, otteretro-catalogo#153).</summary>
/// <param name="Reason">gone, moved, archived, noRelease, noAsset o pickFile (un port con varios archivos sin uno claro).</param>
/// <param name="Proposed">noAsset: el patrón que saldría del único archivo de Windows de la release.</param>
/// <param name="Files">pickFile: los archivos de la release entre los que elegir.</param>
public record RecipeNotice(long Id, string Recipe, string Reason, string Repo, string? To, string? Tag, string? Pattern, string? Proposed, List<string> Files);

/// <summary>Lo que la tarea «Emuladores» (#97) deja en Emuladores (otteretro-catalogo#153): los forks como recetas sin publicar y los avisos en su tarjeta.</summary>
public static class EmulatorReview
{
    /// <summary>El juego del catálogo que se llama así (nombre en español u original, sin distinguir mayúsculas), si es uno solo (#121).</summary>
    // ponytail: igualdad exacta del nombre; si los nombres de las fuentes no calzan, el gameId se elige después en el editor.
    static long? GameByName(System.Data.IDbConnection c, string name) =>
        c.Query<long>("SELECT id FROM game WHERE name_es = @name COLLATE NOCASE OR original_name = @name COLLATE NOCASE LIMIT 2", new { name }).ToList() is [var id] ? id : null;

    static string? Name(Database db, string id) => Recipes.Get(db, id) is { } json ? (string?)JsonNode.Parse(json)!["name"] : null;

    public static List<RecipeNotice> Notices(Database db)
    {
        using var c = db.Open();
        return c.Query<(long Id, string Data)>("SELECT id, data FROM review_item WHERE entity = 'recipe' AND status = 'open' ORDER BY id").Select(r =>
        {
            var j = JsonNode.Parse(r.Data)!;
            var recipe = (string)j["recipe"]!;
            return new RecipeNotice(r.Id, recipe, (string)j["reason"]!, (string?)j["repo"] ?? "", (string?)j["to"], (string?)j["tag"],
                (string?)j["pattern"], (string?)j["proposed"], j["files"]?.AsArray().Select(f => (string)f!).ToList() ?? []);
        }).ToList();
    }

    /// <summary>La receta del catálogo con ese repo que corre esa consola (#131): la que se creó desde «Agregar una receta».</summary>
    static string? RecipeForRepo(Database db, string repo, string console)
    {
        using var c = db.Open();
        return c.Query<string>("SELECT json FROM recipe WHERE kind = 'emulator' ORDER BY id").Select(json => JsonNode.Parse(json)!)
            .FirstOrDefault(r => string.Equals(Recipes.RepoUrl(r), repo, StringComparison.OrdinalIgnoreCase)
                && r["consoles"]?.AsArray().Any(k => (string?)k == console) == true) is { } found ? (string)found["id"]! : null;
    }

    /// <summary>Las recomendaciones de «Mejor emulador» (#130) por revisar (#131).</summary>
    public static List<EmulatorPick> Picks(Database db)
    {
        List<(long Id, string Data)> rows;
        using (var c = db.Open())
            rows = c.Query<(long, string)>("SELECT id, data FROM review_item WHERE entity = @Entity AND status = 'open' ORDER BY id", new { BestEmulator.Entity }).ToList();
        return rows.Select(r =>
        {
            var j = JsonNode.Parse(r.Data)!;
            var console = (string)j["console"]!;
            var repo = (string?)j["repo"];
            var recipe = (string?)j["recipe"] ?? (repo is null ? null : RecipeForRepo(db, repo, console));
            return new EmulatorPick(r.Id, (long)j["gameId"]!, (string?)j["game"] ?? "", console, recipe, recipe is null ? null : Name(db, recipe), repo,
                (string?)j["source"] ?? "", (string?)j["reason"] ?? "");
        }).ToList();
    }

    /// <summary>
    /// Acepta una recomendación (#131): suma <c>{ game, gameId, source }</c> al <c>recommendedFor</c> de su receta y lo saca de las demás recetas
    /// de esa consola que recomendaban el juego (un solo recomendado por juego y consola). Queda sin publicar, como cualquier receta guardada.
    /// Found es false si no existe o ya se resolvió; sin receta (solo un repo) devuelve el error: se agrega primero con «Agregar una receta».
    /// </summary>
    public static async Task<(bool Found, ApiError? Error)> AcceptPick(Database db, long id, string? token, CancellationToken ct = default)
    {
        if (Picks(db).FirstOrDefault(p => p.Id == id) is not { } pick) return (false, null);
        if (pick.Recipe is null || Recipes.Get(db, pick.Recipe) is not { } json) return (true, ApiError.Of("review.pick.noRecipe", ("repo", pick.Repo ?? pick.Recipe ?? "")));
        var target = JsonNode.Parse(json)!.AsObject();
        var item = new JsonObject { ["game"] = pick.Game, ["gameId"] = pick.GameId, ["source"] = pick.Source };
        var list = target["recommendedFor"]?.AsArray() ?? [];
        target["recommendedFor"] = new JsonArray([.. list.Where(g => (long?)g?["gameId"] != pick.GameId).Select(g => g!.DeepClone()), item]);
        // La receta nueva primero: si no se puede guardar (esquema o release), no cambia nada.
        if (await Recipes.Save(db, pick.Recipe, target.ToJsonString(), false, token, ct) is { } error) return (true, error);

        using var c = db.Open();
        // shortcut: recommendedFor no dice la consola; una receta de varias consolas pierde el juego en todas, si hace falta guardar la consola en cada entrada.
        foreach (var other in c.Query<string>("SELECT json FROM recipe WHERE id <> @Recipe", new { pick.Recipe }).Select(x => JsonNode.Parse(x)!.AsObject()))
        {
            if (other["consoles"]?.AsArray().Any(k => (string?)k == pick.Console) != true || other["recommendedFor"] is not JsonArray games
                || !games.Any(g => (long?)g?["gameId"] == pick.GameId)) continue;
            var rest = games.Where(g => (long?)g?["gameId"] != pick.GameId).Select(g => g!.DeepClone()).ToArray();
            if (rest.Length > 0) other["recommendedFor"] = new JsonArray(rest); else other.Remove("recommendedFor");
            using var doc = JsonDocument.Parse(other.ToJsonString());
            Recipes.Store(c, doc.RootElement, JsonSerializer.Serialize(doc.RootElement, Recipes.Indented).ReplaceLineEndings("\n") + "\n");
        }
        c.Execute("UPDATE review_item SET status = 'approved' WHERE id = @id", new { id });
        return (true, null);
    }

    /// <summary>Descarta un aviso o rechaza un candidato. Un fork rechazado queda visto: no vuelve a salir. False si no existe o ya se resolvió.</summary>
    public static bool Dismiss(Database db, long id)
    {
        using var c = db.Open();
        var data = c.ExecuteScalar<string?>("SELECT data FROM review_item WHERE id = @id AND entity IN ('recipe', 'fork', 'emulatorPick') AND status = 'open'", new { id });
        if (data is null) return false;
        c.Execute("UPDATE review_item SET status = 'rejected' WHERE id = @id", new { id });
        if ((string?)JsonNode.Parse(data)!["url"] is { } url)
            c.Execute("UPDATE fork_seen SET result = 'rejected' WHERE repo = @repo", new { repo = Regex.Replace(url, "^https?://", "").ToLowerInvariant() });
        return true;
    }

    /// <summary>
    /// Cada fork que pasó los filtros pasa a ser una receta experimental sin publicar (otteretro-catalogo#153). La que no se puede guardar
    /// queda en el registro y no se reintenta; descartar la tarjeta la borra y el fork no vuelve (queda en <c>fork_seen</c>). Cuántas creó.
    /// </summary>
    public static async Task<int> AcceptForks(TaskRun run, string? token, CancellationToken ct)
    {
        var db = run.Db;
        List<long> ids;
        using (var c = db.Open()) ids = c.Query<long>("SELECT id FROM review_item WHERE entity = 'fork' AND status = 'open' ORDER BY id").ToList();
        var created = 0;
        foreach (var id in ids)
        {
            if (await Accept(db, id, token, ct) is (true, null)) { created++; continue; }
            using var c = db.Open();
            var repo = c.ExecuteScalar<string>("SELECT json_extract(data, '$.repo') FROM review_item WHERE id = @id", new { id });
            c.Execute("UPDATE review_item SET status = 'rejected' WHERE id = @id", new { id });
            run.Log(ApiError.Of("emulators.log.forkFailed", ("repo", repo ?? "")), "warn");
        }
        return created;
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

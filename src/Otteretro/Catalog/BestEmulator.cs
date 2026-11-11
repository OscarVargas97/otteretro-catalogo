using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// La tarea «Mejor emulador» de un juego (#130, plan de #128): Sonnet con búsqueda web elige, en cada consola donde existe el juego,
/// el emulador que recomienda la comunidad, con receta del catálogo o, si no la hay, con su repo. Cada recomendación va a Revisar como
/// <c>review_item</c> con entidad <c>emulatorPick</c>. Nunca recorre todo el catálogo: solo el juego que se pide.
/// </summary>
public static class BestEmulator
{
    public const string Kind = "emulators.best", PromptName = "mejor-emulador", Model = "sonnet", Entity = "emulatorPick";

    /// <param name="Recipe">El id de la receta; vacío si el emulador no tiene receta.</param>
    /// <param name="Repo">El repo del emulador cuando no hay receta; vacío si la hay.</param>
    public record Pick(string Console, string Recipe, string Repo, string Source, string Reason);
    public record Reply(List<Pick> Picks);
    /// <summary>Un juego del selector (#133), con la fecha de su última búsqueda (null si nunca).</summary>
    public record BestEmulatorGame(long Id, string Name, int? Year, string? Saga, string[] Platforms, string? SearchedAt);

    /// <summary>Encola la búsqueda de un juego; si ya está en la cola, devuelve esa. Null si el juego no existe.</summary>
    public static long? Enqueue(Database db, long gameId)
    {
        var data = gameId.ToString(CultureInfo.InvariantCulture);
        using (var c = db.Open())
        {
            if (c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE id = @gameId", new { gameId }) == 0) return null;
            if (c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = @Kind AND data = @data AND status IN ('queued', 'running', 'paused') LIMIT 1", new { Kind, data }) is { } queued)
                return queued;
        }
        ClaudeBudget.AllowNow(db); // la pide la persona: corre ya, también fuera del horario, como «Ejecutar»
        return TaskQueue.Enqueue(db, Kind, data);
    }

    /// <summary>Encola una búsqueda por juego elegido (#133); devuelve cuántos quedaron en la cola. Los que no existen se saltan.</summary>
    public static int Enqueue(Database db, IEnumerable<long> gameIds) => gameIds.Distinct().Count(id => Enqueue(db, id) is not null);

    /// <summary>Los juegos de las sagas elegidas más los juegos sueltos (#133): nunca todo el catálogo.</summary>
    public static List<BestEmulatorGame> Choices(Database db, long[] sagas, long[] games)
    {
        using var c = db.Open();
        return c.Query<(long Id, string Name, int? Year, string? Saga, string? Platforms, string? SearchedAt)>("""
            SELECT g.id, g.name_es, g.year, s.name, (SELECT group_concat(DISTINCT v.platform) FROM version v WHERE v.game_id = g.id), e.searched_at
            FROM game g LEFT JOIN saga s ON s.id = g.saga_id LEFT JOIN emulator_search e ON e.game_id = g.id
            WHERE g.saga_id IN @sagas OR g.id IN @games
            ORDER BY s.name IS NULL, s.name, g.year IS NULL, g.year, g.name_es
            """, new { sagas, games }).Select(r => new BestEmulatorGame(r.Id, r.Name, r.Year, r.Saga, r.Platforms?.Split(',') ?? [], r.SearchedAt)).ToList();
    }

    /// <summary>Cuándo se buscó por última vez el mejor emulador de un juego; null si nunca.</summary>
    public static string? LastSearch(Database db, long gameId)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT searched_at FROM emulator_search WHERE game_id = @gameId", new { gameId });
    }

    public static async Task Run(TaskRun run, CancellationToken ct)
    {
        var db = run.Db;
        var gameId = long.Parse(run.Data!, CultureInfo.InvariantCulture);
        (string Name, string? Original, int? Year) game;
        List<string> consoles;
        List<JsonNode> recipes;
        using (var c = db.Open())
        {
            game = c.QuerySingleOrDefault<(string, string?, int?)>("SELECT name_es, original_name, year FROM game WHERE id = @gameId", new { gameId });
            if (game.Name is null) throw new TaskException(ApiError.Of("emulators.best.noGame"));
            consoles = c.Query<string>("SELECT DISTINCT platform FROM version WHERE game_id = @gameId ORDER BY platform", new { gameId }).ToList();
            recipes = c.Query<string>("SELECT json FROM recipe WHERE kind = 'emulator' ORDER BY id").Select(json => JsonNode.Parse(json)!).ToList();
        }
        // Solo las recetas de sus consolas, con lo que el prompt necesita.
        var catalog = recipes
            .Select(r => new
            {
                id = (string)r["id"]!,
                name = (string?)r["name"] ?? "",
                repo = Recipes.RepoUrl(r) ?? "",
                forkOf = (string?)r["forkOf"] ?? "",
                consoles = r["consoles"]?.AsArray().Select(k => (string)k!).Where(consoles.Contains).ToList() ?? []
            })
            .Where(r => r.consoles.Count > 0).ToList();

        run.Report(10, ApiError.Of("emulators.best.searching", ("game", game.Name), ("consoles", consoles.Count.ToString(CultureInfo.InvariantCulture))));
        var added = 0;
        if (consoles.Count > 0)
        {
            var input = new { game = new { id = gameId, name = game.Name, originalName = game.Original ?? "", year = game.Year }, consoles, recipes = catalog };
            var reply = await ClaudeRunner.Run<Reply>(db, run.Id, PromptName, ClaudeRunner.Prompt(PromptName), input, ct, Model);
            foreach (var p in reply.Value.Picks)
            {
                // Lo que no calza con la entrada no se guarda: otra consola, una receta que no corre esa consola o sin fuente.
                var recipe = p.Recipe.Trim();
                var repo = recipe.Length > 0 ? "" : p.Repo.Trim().TrimEnd('/');
                if (!consoles.Contains(p.Console) || recipe.Length > 0 && !catalog.Any(r => r.id == recipe && r.consoles.Contains(p.Console))
                    || recipe.Length == 0 && !IsUrl(repo) || !IsUrl(p.Source))
                {
                    run.Log(ApiError.Of("emulators.best.log.invalid", ("console", p.Console), ("pick", recipe.Length > 0 ? recipe : repo)), "warn");
                    continue;
                }
                if (ToReview(db, gameId, game.Name, p with { Recipe = recipe, Repo = repo })) added++;
                else run.Log(ApiError.Of("emulators.best.log.known", ("console", p.Console), ("pick", recipe.Length > 0 ? recipe : repo)));
            }
        }
        using (var c = db.Open())
            c.Execute("""
                INSERT INTO emulator_search (game_id, searched_at) VALUES (@gameId, @now)
                ON CONFLICT (game_id) DO UPDATE SET searched_at = excluded.searched_at
                """, new { gameId, now = DateTime.UtcNow.ToString("O") });
        run.Log(ApiError.Of("emulators.best.log.done", ("game", game.Name), ("picks", added.ToString(CultureInfo.InvariantCulture))));
    }

    static bool IsUrl(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "https" or "http";

    /// <summary>
    /// Una recomendación a Revisar. False si ya hay una para el mismo juego, consola y receta (o repo), abierta, aceptada o descartada:
    /// no vuelve a entrar.
    /// </summary>
    static bool ToReview(Database db, long gameId, string game, Pick p)
    {
        using var c = db.Open();
        var known = c.ExecuteScalar<long>("""
            SELECT count(*) FROM review_item WHERE entity = @Entity AND entity_id = @gameId AND json_extract(data, '$.console') = @Console
              AND lower(COALESCE(NULLIF(json_extract(data, '$.recipe'), ''), json_extract(data, '$.repo'))) = lower(@key)
            """, new { Entity, gameId, p.Console, key = p.Recipe.Length > 0 ? p.Recipe : p.Repo }) > 0;
        if (known) return false;
        var data = new { gameId, game, console = p.Console, recipe = p.Recipe.Length > 0 ? p.Recipe : null, repo = p.Repo.Length > 0 ? p.Repo : null, source = p.Source, reason = p.Reason };
        c.Execute("INSERT INTO review_item (kind, entity, entity_id, data, created_at) VALUES ('doubtful', @Entity, @gameId, @data, @now)",
            new { Entity, gameId, data = JsonSerializer.Serialize(data, JsonSerializerOptions.Web), now = DateTime.UtcNow.ToString("O") });
        return true;
    }
}

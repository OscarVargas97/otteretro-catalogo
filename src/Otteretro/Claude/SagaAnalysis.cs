using System.Text.Json;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <summary>
/// Lo que prepara Claude para cada saga (F2.5, #68): las tres rutas, lo icónico, la mejor versión de cada juego y la descripción
/// en español. La clase de cada versión sale de IGDB (#50); Claude la revisa solo si se le pide. Va por lotes de varias sagas por llamada, cita fuentes y todo queda en
/// Revisar (`review_item` por aprobar): nada se aplica sin ti. Lo que no cambió no se vuelve a pedir (F2.4), así que una
/// tarea cortada sigue donde quedó.
/// </summary>
public static class SagaAnalysis
{
    public const string Kind = "claude.sagas";
    public const string Routes = "ruta-saga", Iconic = "iconico", BestVersion = "mejor-version", Classify = "clasificar-version", Translate = "traducir-descripcion";

    /// <summary>Sagas por vuelta: cada vuelta corre los cinco prompts y deja el punto de control.</summary>
    public static int ChunkSagas { get; set; } = 10;

    public record Source(string Title, string Url);

    public record RouteStep(long GameId, long? VersionId, string Reason);
    /// <param name="Kind">release, story o community.</param>
    public record Route(string Kind, string Rationale, List<RouteStep> Steps, List<Source> Sources);
    public record SagaRoutes(long SagaId, List<Route> Routes);
    public record RoutesReply(List<SagaRoutes> Sagas);

    public record SagaIconic(long SagaId, bool Iconic, List<long> GameIds, string Reason, List<Source> Sources);
    public record IconicReply(List<SagaIconic> Sagas);

    /// <param name="Score">1 a 5 dentro del juego: 5 es la mejor versión en ese criterio.</param>
    public record Criterion(int Score, string Text);
    public record VersionScore(long VersionId, Criterion Content, Criterion Gameplay, Criterion Technical, Criterion Original);
    public record GameBest(long GameId, long BestVersionId, string Reason, List<VersionScore> Versions);
    public record BestReply(List<GameBest> Games);

    /// <param name="Kind">original, remaster o remake.</param>
    public record VersionKind(long VersionId, string Kind, string Reason);
    public record ClassifyReply(List<VersionKind> Versions);

    public record Translation(long GameId, string Text);
    public record TranslateReply(List<Translation> Games);

    /// <param name="Prompts">Solo estos prompts (null = todos), como «Reevaluar icónicas» (F2.15).</param>
    record Job(List<long> Pending, int Total, List<string>? Prompts = null);

    record Version(long Id, long GameId, string Platform, string Kind, long? Year);
    record Game(long Id, long SagaId, string Name, long? Year, List<Version> Versions, string? Summary);
    record Saga(long Id, string Name, List<Game> Games, bool Iconic = false);

    /// <summary>Un resultado para Revisar.</summary>
    record Item(string Entity, long EntityId, object Result);

    /// <summary>Encola el análisis de esas sagas, o de todas (primero las tuyas, las icónicas y las más grandes).</summary>
    public static long Enqueue(Database db, IEnumerable<long>? sagaIds = null)
    {
        var ids = sagaIds?.ToList();
        return TaskQueue.Enqueue(db, Kind, ids is null ? null : JsonSerializer.Serialize(new Job(ids, ids.Count), JsonSerializerOptions.Web));
    }

    /// <summary>
    /// «Reevaluar sagas icónicas» (F2.15, #85): olvida lo que Claude dijo de lo icónico de esas sagas (todas, o las que tienen
    /// juegos en esa consola) y encola solo ese prompt. Null si la consola no tiene sagas.
    /// </summary>
    public static long? EnqueueIconic(Database db, string? console = null)
    {
        List<long> ids;
        if (console is null) ids = Start(db, null).Pending;
        else
            using (var c = db.Open())
                ids = c.Query<long>("""
                    SELECT DISTINCT g.saga_id FROM game g JOIN version v ON v.game_id = g.id WHERE v.platform = @console AND g.saga_id IS NOT NULL
                    """, new { console }).ToList();
        if (ids.Count == 0) return null;
        foreach (var id in ids) ClaudeFingerprint.Forget(db, Iconic, $"saga:{id}");
        return TaskQueue.Enqueue(db, Kind, JsonSerializer.Serialize(new Job(ids, ids.Count, [Iconic]), JsonSerializerOptions.Web));
    }

    /// <summary>Un solo paso sobre todo el catálogo (#58): rutas, icónico, mejor versión o traducir, como tarea propia.</summary>
    public static long EnqueueStep(Database db, string prompt)
    {
        var ids = Start(db, null).Pending;
        return TaskQueue.Enqueue(db, Kind, JsonSerializer.Serialize(new Job(ids, ids.Count, [prompt]), JsonSerializerOptions.Web));
    }

    /// <summary>Si una tarea `claude.sagas` es solo ese paso, según su punto de control.</summary>
    public static bool IsStep(string? data, string prompt)
    {
        if (data is null) return false;
        try { return JsonSerializer.Deserialize<Job>(data, JsonSerializerOptions.Web)?.Prompts is [var only] && only == prompt; }
        catch (JsonException) { return false; }
    }

    public static Task Run(TaskRun run, CancellationToken ct) =>
        Process(run, run.Data, null, (progress, detail, data) => run.Report(progress, detail, data), ct);

    /// <summary>El análisis de todas las sagas o solo de <paramref name="only"/> (una parte de la carga, F2.8).</summary>
    /// <param name="data">Punto de control (null = empezar).</param>
    public static async Task Process(TaskRun run, string? data, HashSet<long>? only, Action<int, ApiError, string> report, CancellationToken ct)
    {
        var job = data is null ? Start(run.Db, only) : JsonSerializer.Deserialize<Job>(data, JsonSerializerOptions.Web)!;
        while (job.Pending.Count > 0)
        {
            var chunk = job.Pending.Take(ChunkSagas).ToList();
            var sagas = Load(run.Db, chunk);
            var games = sagas.SelectMany(s => s.Games).ToList();
            var multi = games.Where(g => g.Versions.Count > 1).ToList(); // con una sola versión no hay nada que comparar

            bool Wants(string prompt) => job.Prompts is null || job.Prompts.Contains(prompt);
            // Lo icónico va antes que las rutas: de eso depende a qué sagas se les piden historia y comunidad (#54).
            if (Wants(Iconic)) await Step<Saga, IconicReply>(run, Iconic, "sonnet", 20, sagas, s => $"saga:{s.Id}", SagaInput, IconicItems, ct);
            if (Wants(Routes))
            {
                // La de lanzamiento sale del año, sin Claude, para todas; historia y comunidad, con Sonnet, solo para las icónicas
                // (#54, decisión 279).
                ReleaseRoutes(run.Db, sagas);
                await Step<Saga, RoutesReply>(run, Routes, "sonnet", 3, sagas.Where(s => s.Games.Count > 1 && s.Iconic), s => $"saga:{s.Id}", SagaInput, RouteItems, ct);
            }
            if (Wants(BestVersion)) await Step<Game, BestReply>(run, BestVersion, "sonnet", 30, multi, g => $"game:{g.Id}", VersionInput, BestItems, ct);
            // La clase de cada versión la pone la carga de IGDB (remake 8, remaster 9, ports y ediciones con su original): en la pasada
            // normal Claude ya no la revisa (#50, decisión 279). Solo si se pide a propósito.
            if (job.Prompts?.Contains(Classify) == true)
                await Step<Game, ClassifyReply>(run, Classify, "haiku", 50, multi, g => $"game:{g.Id}", VersionInput, ClassifyItems, ct);
            if (Wants(Translate)) await Step<Game, TranslateReply>(run, Translate, "haiku", 30, games.Where(g => g.Summary is not null), g => $"game:{g.Id}",
                g => new { id = g.Id, name = g.Name, summary = g.Summary }, TranslateItems, ct);

            job = job with { Pending = job.Pending.Skip(chunk.Count).ToList() };
            var done = job.Total - job.Pending.Count;
            report(done * 100 / job.Total, ApiError.Of("tasks.claude.sagas", ("done", done.ToString()), ("total", job.Total.ToString())),
                JsonSerializer.Serialize(job, JsonSerializerOptions.Web));
        }
    }

    static Job Start(Database db, HashSet<long>? only)
    {
        using var c = db.Open();
        var ids = c.Query<long>("""
            SELECT s.id FROM saga s JOIN game g ON g.saga_id = s.id GROUP BY s.id
            ORDER BY EXISTS (SELECT 1 FROM source so JOIN version v ON v.id = so.version_id JOIN game g2 ON g2.id = v.game_id WHERE g2.saga_id = s.id) DESC,
                     s.iconic DESC, count(*) DESC, s.id
            """).Where(id => only is null || only.Contains(id)).ToList();
        return new Job(ids, ids.Count);
    }

    /// <summary>Los cambiados van a Claude por lotes; cada resultado queda por aprobar y la huella, guardada.</summary>
    static async Task Step<TCase, TReply>(TaskRun run, string prompt, string model, int perCall, IEnumerable<TCase> cases,
        Func<TCase, string> key, Func<TCase, object> input, Func<List<TCase>, TReply, IEnumerable<Item?>> items, CancellationToken ct)
    {
        var todo = cases.Where(c => ClaudeFingerprint.Changed(run.Db, prompt, key(c), input(c))).ToList();
        if (todo.Count == 0) return;
        var text = ClaudeRunner.Prompt(prompt);
        foreach (var batch in todo.Chunk(perCall).Select(b => b.ToList()))
        {
            var reply = await ClaudeRunner.Run<TReply>(run.Db, run.Id, prompt, text, new { items = batch.Select(input) }, ct, model);
            var saved = Save(run.Db, prompt, items(batch, reply.Value));
            run.Log(ApiError.Of($"tasks.log.review.{prompt}", ("count", saved.ToString())));
            foreach (var c in batch) ClaudeFingerprint.Save(run.Db, prompt, key(c), input(c));
        }
    }

    /// <summary>Un resultado nuevo reemplaza al que seguía abierto del mismo prompt; lo inventado (ids fuera del lote) se descarta.</summary>
    static int Save(Database db, string prompt, IEnumerable<Item?> items)
    {
        using var c = db.Open();
        using var t = c.BeginTransaction();
        var saved = 0;
        foreach (var item in items)
        {
            if (item is null)
            {
                FileLog.Write(LogLevel.Warning, "Claude", $"Resultado de {prompt} descartado: ids fuera del lote o datos incompletos.");
                continue;
            }
            c.Execute("""
                DELETE FROM review_item WHERE kind = 'pending_approval' AND status = 'open' AND entity = @Entity AND entity_id = @EntityId
                  AND json_extract(data, '$.prompt') = @prompt
                """, new { item.Entity, item.EntityId, prompt }, t);
            c.Execute("""
                INSERT INTO review_item (kind, entity, entity_id, data, created_at) VALUES ('pending_approval', @Entity, @EntityId, @data, @now)
                """, new
            {
                item.Entity,
                item.EntityId,
                data = JsonSerializer.Serialize(new { prompt, result = item.Result }, JsonSerializerOptions.Web),
                now = DateTime.UtcNow.ToString("O")
            }, t);
            saved++;
        }
        t.Commit();
        return saved;
    }

    /// <summary>Con esto se reconoce la ruta de lanzamiento que puso el script: la de Claude o la editada a mano no se tocan.</summary>
    public const string ScriptRationale = "Orden de lanzamiento, por año.";

    /// <summary>
    /// La ruta de lanzamiento de cada saga con más de un juego, por año (#54). Respeta lo ya analizado: no toca una ruta que
    /// propuso Claude o que editaste, ni una saga con rutas esperando en Revisar. La del script se rehace si cambian sus juegos.
    /// </summary>
    static void ReleaseRoutes(Database db, List<Saga> sagas)
    {
        using var c = db.Open();
        using var t = c.BeginTransaction();
        foreach (var saga in sagas.Where(s => s.Games.Count > 1))
        {
            if (Catalog.Editing.IsMarked(c, t, "saga", saga.Id, Catalog.Editing.RouteMark("release"))) continue;
            var existing = c.QueryFirstOrDefault<(long Id, string? Rationale)?>(
                "SELECT id, rationale FROM route WHERE saga_id = @Id AND kind = 'release'", new { saga.Id }, t);
            if (existing is { } e && e.Rationale != ScriptRationale) continue; // de Claude o tuya
            if (c.ExecuteScalar<long>("""
                SELECT count(*) FROM review_item WHERE kind = 'pending_approval' AND status = 'open' AND entity = 'saga' AND entity_id = @Id
                  AND json_extract(data, '$.prompt') = @Routes
                """, new { saga.Id, Routes }, t) > 0) continue; // lo que propuso Claude espera en Revisar
            var games = saga.Games.Select(g => g.Id).ToList(); // ya vienen por año (los sin año al final)
            if (existing is { } old)
            {
                if (c.Query<long>("SELECT game_id FROM route_step WHERE route_id = @Id ORDER BY position", new { old.Id }, t).SequenceEqual(games)) continue;
                c.Execute("DELETE FROM route_step WHERE route_id = @Id; DELETE FROM route WHERE id = @Id", new { old.Id }, t);
            }
            var routeId = c.ExecuteScalar<long>("""
                INSERT INTO route (saga_id, kind, rationale, sources, approved) VALUES (@Id, 'release', @ScriptRationale, '[]', 1) RETURNING id
                """, new { saga.Id, ScriptRationale }, t);
            var position = 0;
            foreach (var game in saga.Games)
                c.Execute("INSERT INTO route_step (route_id, position, game_id, reason) VALUES (@routeId, @position, @Id, @year)",
                    new { routeId, position = ++position, game.Id, year = game.Year?.ToString() }, t);
        }
        t.Commit();
    }

    static object SagaInput(Saga s) => new
    {
        id = s.Id,
        name = s.Name,
        games = s.Games.Select(g => $"{g.Id}|{g.Name}|{g.Year?.ToString() ?? "?"}|{string.Join(',', g.Versions.Select(v => $"{v.Id}:{v.Platform}:{v.Kind}"))}"),
    };

    static object VersionInput(Game g) => new
    {
        id = g.Id,
        name = g.Name,
        versions = g.Versions.Select(v => $"{v.Id}|{v.Platform}|{v.Kind}|{v.Year?.ToString() ?? "?"}"),
    };

    static IEnumerable<Item?> RouteItems(List<Saga> batch, RoutesReply reply) => reply.Sagas.Select(r =>
    {
        var saga = batch.FirstOrDefault(s => s.Id == r.SagaId);
        // La de lanzamiento la pone el script (#54): si Claude la manda igual, se ignora.
        var routes = r.Routes.Where(route => route.Kind != "release").ToList();
        var ok = saga is not null && routes.Count > 0 && routes.All(route =>
            route.Kind is "story" or "community" && route.Steps.Count > 0 && route.Steps.All(step =>
                saga.Games.FirstOrDefault(g => g.Id == step.GameId) is { } game
                && (step.VersionId is null || game.Versions.Any(v => v.Id == step.VersionId))));
        return ok ? new Item("saga", r.SagaId, routes) : null;
    });

    static IEnumerable<Item?> IconicItems(List<Saga> batch, IconicReply reply) => reply.Sagas.Select(r =>
        batch.FirstOrDefault(s => s.Id == r.SagaId) is { } saga && r.GameIds.All(id => saga.Games.Any(g => g.Id == id))
            ? new Item("saga", r.SagaId, r) : null);

    static IEnumerable<Item?> BestItems(List<Game> batch, BestReply reply) => reply.Games.Select(r =>
    {
        var ids = batch.FirstOrDefault(g => g.Id == r.GameId)?.Versions.Select(v => v.Id).ToHashSet();
        var ok = ids is not null && ids.Contains(r.BestVersionId) && r.Versions.All(v => ids.Contains(v.VersionId))
                 && r.Versions.SelectMany(v => new[] { v.Content, v.Gameplay, v.Technical, v.Original }).All(c => c.Score is >= 1 and <= 5);
        return ok ? new Item("game", r.GameId, r) : null;
    });

    /// <summary>Solo las versiones que Claude clasificaría distinto que IGDB.</summary>
    static IEnumerable<Item?> ClassifyItems(List<Game> batch, ClassifyReply reply)
    {
        var versions = batch.SelectMany(g => g.Versions).ToDictionary(v => v.Id);
        foreach (var r in reply.Versions)
        {
            if (!versions.TryGetValue(r.VersionId, out var v) || r.Kind is not ("original" or "remaster" or "remake")) yield return null;
            else if (r.Kind != v.Kind) yield return new Item("version", r.VersionId, new { r.Kind, r.Reason, before = v.Kind });
        }
    }

    static IEnumerable<Item?> TranslateItems(List<Game> batch, TranslateReply reply) => reply.Games.Select(r =>
        batch.Any(g => g.Id == r.GameId) && !string.IsNullOrWhiteSpace(r.Text) ? new Item("game", r.GameId, new { text = r.Text.Trim() }) : null);

    static List<Saga> Load(Database db, List<long> ids)
    {
        using var c = db.Open();
        var versions = c.Query<(long Id, long GameId, string Platform, string Kind, long? Year)>("""
            SELECT v.id, v.game_id, v.platform, v.kind, v.year FROM version v JOIN game g ON g.id = v.game_id WHERE g.saga_id IN @ids ORDER BY v.year, v.id
            """, new { ids }).Select(v => new Version(v.Id, v.GameId, v.Platform, v.Kind, v.Year)).ToLookup(v => v.GameId);
        // La descripción de IGDB, mientras no tenga traducción aprobada.
        var summaries = c.Query<(long Id, string Text)>("""
            SELECT f.entity_id, f.value FROM field_value f JOIN game g ON g.id = f.entity_id
            WHERE f.entity = 'game' AND f.field = 'summary' AND f.source = 'igdb' AND g.saga_id IN @ids
              AND NOT EXISTS (SELECT 1 FROM field_value e WHERE e.entity = 'game' AND e.entity_id = f.entity_id AND e.field = 'summary_es')
            """, new { ids }).ToDictionary(s => s.Id, s => s.Text);
        var games = c.Query<(long Id, long SagaId, string Name, long? Year)>("""
            SELECT id, saga_id, COALESCE(original_name, name_es), year FROM game WHERE saga_id IN @ids ORDER BY year IS NULL, year, id
            """, new { ids }).Select(g => new Game(g.Id, g.SagaId, g.Name, g.Year, versions[g.Id].ToList(), summaries.GetValueOrDefault(g.Id)))
            .ToLookup(g => g.SagaId);
        var names = c.Query<(long Id, string Name, bool Iconic)>("SELECT id, name, iconic FROM saga WHERE id IN @ids", new { ids }).ToDictionary(s => s.Id);
        return ids.Where(names.ContainsKey).Select(id => new Saga(id, names[id].Name, games[id].ToList(), names[id].Iconic)).Where(s => s.Games.Count > 0).ToList();
    }
}

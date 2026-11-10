using System.Text.Json;
using Dapper;
using Otteretro.Catalog;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <param name="Result">El resultado de Claude tal cual (JSON); su forma depende de <paramref name="Prompt"/>.</param>
/// <param name="Title">De qué es: la saga, el juego o «juego · consola».</param>
public record ReviewItem(long Id, string Prompt, string Entity, long EntityId, string Title, string Result, string CreatedAt);

/// <param name="Change">move, merge o rename.</param>
/// <param name="Detail">El cambio tal cual (JSON).</param>
/// <summary>Un cambio de Claude en claro (F2.12): qué juegos, de qué saga a cuál, qué se unió o cómo se llamaba.</summary>
/// <param name="From">Las sagas donde estaban los juegos movidos ("" = sueltos).</param>
/// <param name="ToKind">saga, new (saga nueva) o none (quedan sueltos).</param>
/// <param name="Rules">Las correcciones que escribió la persona sobre este cambio.</param>
public record LearningItem(long Id, string Change, string? Reason, string Detail, string CreatedAt, bool Undone, long? TaskId, long? SagaId,
    string? SagaName, List<string> Games, List<string> From, string? To, string? ToKind, string? Kept, string? NameBefore, string? NameAfter, List<string> Rules);
public record CorrectRequest(string Note);

/// <param name="ActiveLearnings">Cuántos cambios de Claude siguen vigentes (sin deshacer), para la pestaña Aprendizajes.</param>
/// <param name="Files">Archivos en Dudosas y Sin identificar (#181).</param>
/// <param name="Forks">Candidatos de fork de la tarea «Emuladores» (#98).</param>
/// <param name="Notices">Avisos de recetas de la misma tarea (#98).</param>
public record ReviewPage(List<ReviewItem> Pending, int ActiveLearnings, List<FileReview> Files, List<ForkCandidate> Forks, List<RecipeNotice> Notices, List<PortCandidate> Ports);

/// <param name="Kind">doubtful (Claude lo asignó) o unidentified.</param>
/// <param name="Proposed">El nombre que dio Claude, si dio uno.</param>
/// <param name="GameId">El juego que tiene ahora (en Dudosas).</param>
public record FileReview(long Id, string Kind, long SourceId, string Path, string Console, string? Component, string? TitleId, string? Proposed,
    string? Reason, long? GameId, string? GameName);

/// <param name="GameId">null confirma lo que propuso Claude; un juego lo cambia (o lo elige, en Sin identificar).</param>
public record ResolveFileRequest(long? GameId);
public record LearningPage(List<LearningItem> Items, int Total);

public record SourceRef(string Title, string Url);
public record RouteStepInfo(int Position, long GameId, string Name, int? Year, long? VersionId, string? VersionPlatform, string? Reason,
    string? Status, bool Owned, bool HasCover);
/// <param name="Kind">release, story o community.</param>
public record RouteInfo(long Id, string Kind, string? Rationale, List<SourceRef> Sources, List<RouteStepInfo> Steps);
public record RoutesPage(long? ChosenRoute, List<RouteInfo> Routes);

/// <param name="Criteria">content, gameplay, technical y original: Value es la nota de 1 a 5.</param>
public record CriterionInfo(string Criterion, string? Value, string? Text);
public record VersionCompare(long VersionId, string Platform, string Kind, int? Year, bool Best, List<CriterionInfo> Criteria);

/// <param name="Step">El primer juego de la ruta que no terminaste ni abandonaste.</param>
public record NextGame(long SagaId, string SagaName, string RouteKind, int Done, int Total, RouteStepInfo Step);

/// <summary>
/// Revisar › Resultados por aprobar y Aprendizajes (F2.6): aprobar aplica lo que preparó Claude (F2.5) a sus tablas; las
/// rutas aprobadas se leen en la saga, la comparativa en la ficha y el siguiente juego en Inicio.
/// </summary>
public static class ClaudeReview
{
    public static readonly string[] Criteria = ["content", "gameplay", "technical", "original"];
    static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    public static ReviewPage Page(Database db)
    {
        using var c = db.Open();
        var pending = c.Query<(long Id, string Entity, long EntityId, string Data, string CreatedAt, string? Title)>("""
            SELECT r.id, r.entity, r.entity_id, r.data, r.created_at,
                   CASE r.entity WHEN 'saga' THEN (SELECT name FROM saga WHERE id = r.entity_id)
                                 WHEN 'game' THEN (SELECT name_es FROM game WHERE id = r.entity_id)
                                 ELSE (SELECT g.name_es || ' · ' || v.platform FROM version v JOIN game g ON g.id = v.game_id WHERE v.id = r.entity_id) END
            FROM review_item r WHERE r.kind = 'pending_approval' AND r.status = 'open' ORDER BY r.id
            """).Select(r =>
        {
            using var doc = JsonDocument.Parse(r.Data);
            return new ReviewItem(r.Id, doc.RootElement.GetProperty("prompt").GetString()!, r.Entity, r.EntityId, r.Title ?? "?",
                doc.RootElement.GetProperty("result").GetRawText(), r.CreatedAt);
        }).Where(r => r.Title != "?").ToList(); // lo que ya no existe (una saga unida) no se muestra
        var files = c.Query<(long Id, string Kind, long SourceId, string Root, string Relative, string Console, string? Component, string? TitleId, string Data, long? GameId, string? GameName)>("""
            SELECT r.id, r.kind, o.id, coalesce(l.path, ''), o.relative_path, o.console, o.component, o.title_id, r.data, g.id, g.name_es
            FROM review_item r JOIN source o ON o.id = r.entity_id LEFT JOIN library l ON l.id = o.library_id
            LEFT JOIN version v ON v.id = o.version_id LEFT JOIN game g ON g.id = v.game_id
            WHERE r.entity = 'source' AND r.kind IN ('doubtful', 'unidentified') AND r.status = 'open' ORDER BY o.console, o.relative_path
            """).Select(r =>
        {
            using var doc = JsonDocument.Parse(r.Data);
            string? Text(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new FileReview(r.Id, r.Kind, r.SourceId, Path.Combine(r.Root, r.Relative), r.Console, r.Component, r.TitleId, Text("name"), Text("reason"),
                r.GameId, r.GameName);
        }).ToList();
        return new ReviewPage(pending, c.ExecuteScalar<int>("SELECT count(*) FROM learning WHERE undone = 0"), files, EmulatorReview.Forks(db), EmulatorReview.Notices(db), PortRecipes.List(db));
    }

    /// <summary>
    /// Revisar › Aprendizajes por tramos (#177), lo más nuevo primero. <paramref name="kind"/> filtra por tipo de cambio y
    /// <paramref name="q"/> busca en los nombres en claro (sagas y juegos), que solo existen ya traducidos: con búsqueda se
    /// traducen los últimos 2.000 y se filtra; sin ella, solo el tramo.
    /// </summary>
    public static LearningPage Learnings(Database db, string? kind, string? q, int offset, int limit)
    {
        using var c = db.Open();
        const string Where = "WHERE @kind IS NULL OR coalesce(json_extract(content, '$.change.kind'), json_extract(content, '$.kind')) = @kind";
        var text = q?.Trim().ToLowerInvariant() is { Length: > 0 } t ? t : null;
        if (text is null)
        {
            var total = c.ExecuteScalar<int>($"SELECT count(*) FROM learning {Where}", new { kind });
            var rows = c.Query<(long Id, string Content, string CreatedAt, bool Undone, long? TaskId)>($"SELECT id, content, created_at, undone, task_id FROM learning {Where} ORDER BY id DESC LIMIT @limit OFFSET @offset",
                new { kind, limit, offset });
            return new LearningPage(Describe(c, rows), total);
        }
        var all = Describe(c, c.Query<(long Id, string Content, string CreatedAt, bool Undone, long? TaskId)>($"SELECT id, content, created_at, undone, task_id FROM learning {Where} ORDER BY id DESC LIMIT 2000", new { kind }))
            .Where(l => new[] { l.SagaName, l.To, l.Kept, l.NameBefore, l.NameAfter }.Concat(l.Games).Concat(l.From)
                .Any(x => x?.ToLowerInvariant().Contains(text) == true)).ToList();
        return new LearningPage(all.Skip(offset).Take(limit).ToList(), all.Count);
    }

    /// <summary>Cada cambio en claro. Los nombres se buscan solo para estas filas, no el catálogo entero.</summary>
    static List<LearningItem> Describe(System.Data.IDbConnection c, IEnumerable<(long Id, string Content, string CreatedAt, bool Undone, long? TaskId)> rows)
    {
        var sagas = new Dictionary<long, string?>();
        var games = new Dictionary<long, string?>();
        string? SagaName(long id) => sagas.TryGetValue(id, out var n) ? n : sagas[id] = c.ExecuteScalar<string?>("SELECT name FROM saga WHERE id = @id", new { id });
        string? GameName(long id) => games.TryGetValue(id, out var n) ? n : games[id] = c.ExecuteScalar<string?>("SELECT name_es FROM game WHERE id = @id", new { id });
        var list = rows.ToList();
        var ids = JsonSerializer.Serialize(list.Select(l => l.Id));
        var rules = c.Query<(long LearningId, string Text)>("SELECT learning_id, text FROM saga_rule WHERE learning_id IN (SELECT value FROM json_each(@ids)) ORDER BY id", new { ids })
            .GroupBy(r => r.LearningId).ToDictionary(g => g.Key, g => g.Select(r => r.Text).ToList());
        return list.Select(l =>
        {
            using var doc = JsonDocument.Parse(l.Content);
            var change = doc.RootElement.TryGetProperty("change", out var ch) ? ch : doc.RootElement;
            var item = new LearningItem(l.Id, change.TryGetProperty("kind", out var k) ? k.GetString() ?? "?" : "?",
                change.TryGetProperty("reason", out var why) ? why.GetString() : null, change.GetRawText(), l.CreatedAt, l.Undone, l.TaskId,
                null, null, [], [], null, null, null, null, null, rules.GetValueOrDefault(l.Id) ?? []);
            if (item.Change == "alias")
                return item with { NameBefore = change.GetProperty("text").GetString(), NameAfter = GameName(change.GetProperty("gameId").GetInt64()) };
            if (!doc.RootElement.TryGetProperty("before", out var before)) return item;
            string Saga(long? id) => id is { } x ? SagaName(x) ?? $"#{x}" : "";
            string Game(long id) => GameName(id) ?? $"#{id}";
            var sagaId = CatalogCleanup.SagaOf(c, l.Content);
            item = item with { SagaId = sagaId, SagaName = sagaId is { } sid ? SagaName(sid) : null };
            var c2 = change.Deserialize<CatalogCleanup.Change>(Web)!;
            switch (item.Change)
            {
                case "move":
                    var move = before.Deserialize<CatalogCleanup.MoveBefore>(CatalogCleanup.Tuples)!;
                    return item with
                    {
                        Games = move.Games.Select(g => Game(g.Game)).ToList(),
                        From = move.Games.Select(g => Saga(g.Saga)).Distinct().ToList(),
                        ToKind = c2.ToSagaId is not null ? "saga" : c2.NewSagaName is not null ? "new" : "none",
                        To = c2.ToSagaId is { } to ? Saga(to) : c2.NewSagaName,
                    };
                case "merge":
                    var merge = before.Deserialize<CatalogCleanup.MergeBefore>(CatalogCleanup.Tuples)!;
                    return item with { Kept = Game(merge.Keep), Games = merge.Games.Select(g => g.Year is { } y ? $"{g.NameEs} ({y})" : g.NameEs).ToList() };
                case "rename":
                    var rename = before.Deserialize<CatalogCleanup.RenameBefore>(CatalogCleanup.Tuples)!;
                    return item with
                    {
                        NameBefore = rename.Claude ?? c.ExecuteScalar<string?>(
                        "SELECT value FROM field_value WHERE entity = 'saga' AND entity_id = @saga AND field = 'name' AND source = 'igdb'", new { saga = rename.Saga }),
                        NameAfter = c2.Name
                    };
                default:
                    return item;
            }
        }).ToList();
    }

    /// <summary>Aplica un resultado por aprobar. False si no existe o ya se resolvió.</summary>
    /// <param name="editedRoutes">
    /// Las rutas como quedaron en Revisar (F2.21): pasos reordenados o quitados. Solo valen los juegos y las rutas que propuso
    /// Claude; lo demás se ignora.
    /// </param>
    public static bool Approve(Database db, long id, List<SagaAnalysis.Route>? editedRoutes = null)
    {
        using var c = db.Open();
        using var t = c.BeginTransaction();
        var item = c.QuerySingleOrDefault<(string Entity, long EntityId, string Data)?>(
            "SELECT entity, entity_id, data FROM review_item WHERE id = @id AND kind = 'pending_approval' AND status = 'open'", new { id }, t);
        if (item is not { } r) return false;
        using var doc = JsonDocument.Parse(r.Data);
        var result = doc.RootElement.GetProperty("result");
        switch (doc.RootElement.GetProperty("prompt").GetString())
        {
            case SagaAnalysis.Routes:
                var chosen = c.ExecuteScalar<string?>("SELECT kind FROM route WHERE id = (SELECT chosen_route FROM saga_user WHERE saga_id = @EntityId)", new { r.EntityId }, t);
                var proposed = result.Deserialize<List<SagaAnalysis.Route>>(Web)!;
                foreach (var route in editedRoutes is null ? proposed : Edited(proposed, editedRoutes))
                {
                    // Una ruta editada o borrada a mano (#17) no se reemplaza.
                    if (Catalog.Editing.IsMarked(c, t, "saga", r.EntityId, Catalog.Editing.RouteMark(route.Kind))) continue;
                    c.Execute("DELETE FROM route WHERE saga_id = @EntityId AND kind = @Kind", new { r.EntityId, route.Kind }, t);
                    var routeId = c.ExecuteScalar<long>("""
                        INSERT INTO route (saga_id, kind, rationale, sources, approved) VALUES (@EntityId, @Kind, @Rationale, @sources, 1) RETURNING id
                        """, new { r.EntityId, route.Kind, route.Rationale, sources = JsonSerializer.Serialize(route.Sources, Web) }, t);
                    var position = 0;
                    foreach (var step in route.Steps)
                        c.Execute("""
                            INSERT INTO route_step (route_id, position, game_id, recommended_version_id, reason)
                            SELECT @routeId, @position, @GameId, (SELECT id FROM version WHERE id = @VersionId AND game_id = @GameId), @Reason
                            WHERE EXISTS (SELECT 1 FROM game WHERE id = @GameId)
                            """, new { routeId, position = ++position, step.GameId, step.VersionId, step.Reason }, t);
                    // La ruta que seguías se renueva; sin ninguna elegida, se sigue la de la comunidad.
                    if (route.Kind == (chosen ?? "community"))
                        c.Execute("""
                            INSERT INTO saga_user (saga_id, chosen_route) VALUES (@EntityId, @routeId)
                            ON CONFLICT (saga_id) DO UPDATE SET chosen_route = excluded.chosen_route
                            """, new { routeId, r.EntityId }, t);
                }
                break;
            case SagaAnalysis.Iconic:
                var iconic = result.Deserialize<SagaAnalysis.SagaIconic>(Web)!;
                if (Catalog.Editing.IsMarked(c, t, "saga", r.EntityId, "iconic")) break; // se marcó a mano (#17)
                c.Execute("UPDATE saga SET iconic = @Iconic WHERE id = @EntityId", new { iconic.Iconic, r.EntityId }, t);
                // Sus juegos icónicos (#112): de ellos sale la imagen de la saga. Solo los de esta saga.
                c.Execute("UPDATE game SET iconic = (@Iconic AND id IN (SELECT value FROM json_each(@ids))) WHERE saga_id = @EntityId",
                    new { iconic.Iconic, ids = JsonSerializer.Serialize(iconic.GameIds), r.EntityId }, t);
                break;
            case SagaAnalysis.BestVersion:
                var best = result.Deserialize<SagaAnalysis.GameBest>(Web)!;
                foreach (var v in best.Versions)
                    foreach (var (criterion, value) in Criteria.Zip(new[] { v.Content, v.Gameplay, v.Technical, v.Original }))
                        c.Execute("""
                            INSERT INTO version_criterion (version_id, criterion, value, text)
                            SELECT @VersionId, @criterion, @score, @Text WHERE EXISTS (SELECT 1 FROM version WHERE id = @VersionId)
                            ON CONFLICT (version_id, criterion) DO UPDATE SET value = excluded.value, text = excluded.text
                            """, new { v.VersionId, criterion, score = value.Score.ToString(), value.Text }, t);
                Store(c, t, r.EntityId, "best_version", best.BestVersionId.ToString());
                break;
            case SagaAnalysis.Classify:
                c.Execute("UPDATE version SET kind = @kind WHERE id = @EntityId", new { kind = result.GetProperty("kind").GetString(), r.EntityId }, t);
                break;
            case SagaAnalysis.Translate:
                Store(c, t, r.EntityId, "summary_es", result.GetProperty("text").GetString());
                break;
            default:
                return false;
        }
        c.Execute("UPDATE review_item SET status = 'approved' WHERE id = @id", new { id }, t);
        t.Commit();
        return true;
    }

    /// <summary>«Aprobar todas» (#56): aplica cada pendiente como su botón Aprobar; <paramref name="prompt"/> limita a un tipo. Cuántas aplicó.</summary>
    public static int ApproveAll(Database db, string? prompt)
    {
        List<long> ids;
        using (var c = db.Open())
            ids = c.Query<long>("""
                SELECT id FROM review_item WHERE kind = 'pending_approval' AND status = 'open'
                  AND (@prompt IS NULL OR json_extract(data, '$.prompt') = @prompt) ORDER BY id
                """, new { prompt }).ToList();
        return ids.Count(id => Approve(db, id));
    }

    static IEnumerable<SagaAnalysis.Route> Edited(List<SagaAnalysis.Route> proposed, List<SagaAnalysis.Route> edited) =>
        proposed.Select(p => edited.FirstOrDefault(e => e.Kind == p.Kind) is { } e
            ? p with { Steps = e.Steps.Select(s => p.Steps.FirstOrDefault(x => x.GameId == s.GameId)).OfType<SagaAnalysis.RouteStep>().DistinctBy(s => s.GameId).ToList() }
            : p);

    /// <summary>Cuántos resultados esperan aprobación: el contador de Revisar en la barra lateral (D3).</summary>
    public static int PendingCount(Database db)
    {
        using var c = db.Open();
        // Lo de Claude por aprobar y los archivos en Dudosas y Sin identificar (#181).
        return c.ExecuteScalar<int>("SELECT count(*) FROM review_item WHERE status = 'open' AND (kind = 'pending_approval' OR entity = 'source')");
    }

    public static bool Reject(Database db, long id)
    {
        using var c = db.Open();
        return c.Execute("UPDATE review_item SET status = 'rejected' WHERE id = @id AND kind = 'pending_approval' AND status = 'open'", new { id }) > 0;
    }

    /// <summary>Un dato de Claude sin columna propia (descripción, mejor versión): queda en `field_value`.</summary>
    static void Store(System.Data.IDbConnection c, System.Data.IDbTransaction t, long gameId, string field, string? value) =>
        c.Execute("""
            INSERT INTO field_value (entity, entity_id, field, value, source) VALUES ('game', @gameId, @field, @value, 'claude')
            ON CONFLICT (entity, entity_id, field, source) DO UPDATE SET value = excluded.value
            """, new { gameId, field, value }, t);

    public static RoutesPage Routes(Database db, long sagaId)
    {
        using var c = db.Open();
        var chosen = c.ExecuteScalar<long?>("SELECT chosen_route FROM saga_user WHERE saga_id = @sagaId", new { sagaId });
        var covers = Catalog.CatalogQueries.Covers(db);
        var routes = c.Query<(long Id, string Kind, string? Rationale, string? Sources)>(
            "SELECT id, kind, rationale, sources FROM route WHERE saga_id = @sagaId AND approved = 1 ORDER BY CASE kind WHEN 'release' THEN 0 WHEN 'story' THEN 1 ELSE 2 END",
            new { sagaId }).ToList();
        return new RoutesPage(chosen, routes.Select(r => new RouteInfo(r.Id, r.Kind, r.Rationale,
            r.Sources is null ? [] : JsonSerializer.Deserialize<List<SourceRef>>(r.Sources, Web) ?? [], Steps(c, r.Id, covers))).ToList());
    }

    static List<RouteStepInfo> Steps(System.Data.IDbConnection c, long routeId, HashSet<long> covers) =>
        c.Query<(long Position, long GameId, string Name, long? Year, long? VersionId, string? Platform, string? Reason, string? Status, bool Owned)>("""
            SELECT st.position, g.id, g.name_es, g.year, st.recommended_version_id, v.platform, st.reason, p.status,
                   EXISTS (SELECT 1 FROM source o JOIN version x ON x.id = o.version_id WHERE x.game_id = g.id)
            FROM route_step st JOIN game g ON g.id = st.game_id
            LEFT JOIN version v ON v.id = st.recommended_version_id LEFT JOIN progress p ON p.game_id = g.id
            WHERE st.route_id = @routeId ORDER BY st.position
            """, new { routeId }).Select(s => new RouteStepInfo((int)s.Position, s.GameId, s.Name, (int?)s.Year, s.VersionId, s.Platform, s.Reason,
            s.Status, s.Owned, covers.Contains(s.GameId))).ToList();

    /// <summary>Elige qué ruta sigues en una saga (null = ninguna). False si la ruta no es de esa saga.</summary>
    public static bool Choose(Database db, long sagaId, long? routeId)
    {
        using var c = db.Open();
        if (routeId is not null && c.ExecuteScalar<long>("SELECT count(*) FROM route WHERE id = @routeId AND saga_id = @sagaId", new { routeId, sagaId }) == 0)
            return false;
        if (c.ExecuteScalar<long>("SELECT count(*) FROM saga WHERE id = @sagaId", new { sagaId }) == 0) return false;
        c.Execute("""
            INSERT INTO saga_user (saga_id, chosen_route, following) VALUES (@sagaId, @routeId, @routeId IS NOT NULL)
            ON CONFLICT (saga_id) DO UPDATE SET chosen_route = excluded.chosen_route,
                following = CASE WHEN excluded.chosen_route IS NULL THEN following ELSE 1 END
            """, new { routeId, sagaId });
        return true;
    }

    public static List<VersionCompare> Compare(Database db, long gameId)
    {
        using var c = db.Open();
        // Lo de Claude manda; si no la vio, la de las reglas (#51).
        var best = c.ExecuteScalar<string?>("""
            SELECT value FROM field_value WHERE entity = 'game' AND entity_id = @gameId AND field = 'best_version' AND source IN ('claude', 'script')
            ORDER BY source = 'claude' DESC LIMIT 1
            """, new { gameId });
        var criteria = c.Query<(long VersionId, string Criterion, string? Value, string? Text)>(
            "SELECT k.version_id, k.criterion, k.value, k.text FROM version_criterion k JOIN version v ON v.id = k.version_id WHERE v.game_id = @gameId", new { gameId })
            .ToLookup(k => k.VersionId);
        return c.Query<(long Id, string Platform, string Kind, long? Year)>("SELECT id, platform, kind, year FROM version WHERE game_id = @gameId ORDER BY year, id", new { gameId })
            .Select(v => new VersionCompare(v.Id, v.Platform, v.Kind, (int?)v.Year, best == v.Id.ToString(),
                Criteria.Select(k => criteria[v.Id].FirstOrDefault(x => x.Criterion == k) is { Criterion: not null } x ? new CriterionInfo(k, x.Value, x.Text) : new CriterionInfo(k, null, null)).ToList()))
            .ToList();
    }

    /// <summary>Inicio › Siguiente en tus rutas: en cada saga que sigues con una ruta elegida, el primer juego sin terminar.</summary>
    public static List<NextGame> Next(Database db)
    {
        using var c = db.Open();
        var routes = c.Query<(long SagaId, string SagaName, long RouteId, string Kind)>("""
            SELECT s.id, s.name, r.id, r.kind FROM saga_user u JOIN saga s ON s.id = u.saga_id JOIN route r ON r.id = u.chosen_route WHERE u.following = 1 ORDER BY u.favorite DESC, s.name
            """).ToList();
        var next = new List<NextGame>();
        var covers = Catalog.CatalogQueries.Covers(db);
        foreach (var r in routes)
        {
            var steps = Steps(c, r.RouteId, covers);
            var done = steps.Count(s => s.Status is "finished" or "completed" or "dropped");
            if (steps.FirstOrDefault(s => s.Status is not ("finished" or "completed" or "dropped")) is { } step)
                next.Add(new NextGame(r.SagaId, r.SagaName, r.Kind, done, steps.Count, step));
        }
        return next;
    }
}

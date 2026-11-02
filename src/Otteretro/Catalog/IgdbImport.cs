using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Carga del catálogo desde IGDB (F1.5), por partes: una tarea por saga o por consola, de a 500 juegos, y cada
/// página guarda su punto de control. Nunca todo PC de golpe: PC entra solo por sagas (#131).
/// </summary>
public static class IgdbImport
{
    /// <summary>
    /// Plataformas de IGDB → `console.id`, de las consolas habilitadas (tabla `console_platform`, F2.13). Las variantes van con
    /// su consola (GBC con Game Boy, DSi con DS…).
    /// </summary>
    public static Dictionary<long, string> Platforms(System.Data.IDbConnection c, System.Data.IDbTransaction? t = null) =>
        c.Query<(long IgdbId, string ConsoleId)>(
            "SELECT p.igdb_id, p.console_id FROM console_platform p JOIN console k ON k.id = p.console_id WHERE k.enabled = 1", transaction: t)
            .ToDictionary(r => r.IgdbId, r => r.ConsoleId);

    /// <summary>PC y PC VR: se cargan solo por saga y sus versiones llevan el id de Steam.</summary>
    public static readonly string[] PcPlatforms = ["pc", "pcvr"];

    public const int Page = 500;
    const string Source = "igdb";

    /// <summary>Punto de control de «cargar una saga»: lo que se pidió, la saga encontrada y por dónde va.</summary>
    public record SagaJob(string Query, string? Kind = null, long? IgdbId = null, string? Name = null, long Offset = 0);
    public record PlatformJob(string Platform, long Offset = 0);
    public record PopularJob(long Offset = 0);

    /// <summary>Cuántos juegos de PC populares trae `--pc-populares` (#62): los más calificados en IGDB.</summary>
    public const int PopularLimit = 1500;
    public record SagaRef(string Kind, long IgdbId, string? Name);

    public static long EnqueueSaga(Database db, string query) =>
        TaskQueue.Enqueue(db, "igdb.saga", JsonSerializer.Serialize(new SagaJob(query), JsonSerializerOptions.Web));

    public static long EnqueuePopular(Database db) =>
        TaskQueue.Enqueue(db, "igdb.popular", JsonSerializer.Serialize(new PopularJob(), JsonSerializerOptions.Web));

    public static long EnqueuePlatform(Database db, string platform) =>
        TaskQueue.Enqueue(db, "igdb.platform", JsonSerializer.Serialize(new PlatformJob(platform), JsonSerializerOptions.Web));

    /// <summary>Para las pruebas: el manejador HTTP falso de IGDB y Twitch.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    static Igdb Client(TaskRun run) => new(new ApiClient(run.Db, Handler), run.Credentials, Handler);

    public static async Task Saga(TaskRun run, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<SagaJob>(run.Data!, JsonSerializerOptions.Web)!;
        var igdb = Client(run);
        if (job.IgdbId is null)
        {
            var found = await FindSaga(igdb, job.Query, ct) ?? throw new TaskException(ApiError.Of("tasks.igdb.sagaNotFound", ("query", job.Query)));
            job = job with { Kind = found.Kind, IgdbId = found.IgdbId, Name = found.Name ?? job.Query };
        }
        var saga = new SagaRef(job.Kind!, job.IgdbId!.Value, job.Name);
        var where = $"where {(saga.Kind == "franchise" ? "franchises" : "collections")} = ({saga.IgdbId}) & version_parent = null;";
        var total = await igdb.Count("games", where, ct);
        Report(run, job.Offset, total, ApiError.Of("tasks.igdb.saga", ("name", job.Name!), ("done", job.Offset.ToString()), ("total", total.ToString())), job);

        while (true)
        {
            var page = await igdb.Query<IgdbGame>("games", $"{IgdbGame.Fields} {where} sort id asc; limit {Page}; offset {job.Offset};", ct);
            await Import(run.Db, igdb, page, saga, ct);
            job = job with { Offset = job.Offset + page.Length };
            Report(run, job.Offset, total, ApiError.Of("tasks.igdb.saga", ("name", job.Name!), ("done", job.Offset.ToString()), ("total", total.ToString())), job);
            if (page.Length < Page) break;
        }
        QueueFollowUps(run.Db);
    }

    public static async Task Platform(TaskRun run, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<PlatformJob>(run.Data!, JsonSerializerOptions.Web)!;
        if (PcPlatforms.Contains(job.Platform)) throw new TaskException(ApiError.Of("tasks.igdb.pcBySaga"));
        List<long> ids;
        using (var c = run.Db.Open()) ids = Platforms(c).Where(p => p.Value == job.Platform).Select(p => p.Key).ToList();
        if (ids.Count == 0) throw new TaskException(ApiError.Of("tasks.igdb.unknownPlatform", ("platform", job.Platform)));

        var igdb = Client(run);
        var where = $"where platforms = ({string.Join(',', ids)}) & version_parent = null & game_type = ({string.Join(',', IgdbGame.PlayableTypes)});";
        var total = await igdb.Count("games", where, ct);
        while (true)
        {
            var page = await igdb.Query<IgdbGame>("games", $"{IgdbGame.Fields} {where} sort id asc; limit {Page}; offset {job.Offset};", ct);
            await Import(run.Db, igdb, page, null, ct);
            job = job with { Offset = job.Offset + page.Length };
            Report(run, job.Offset, total, ApiError.Of("tasks.igdb.platform", ("platform", job.Platform), ("done", job.Offset.ToString()), ("total", total.ToString())), job);
            if (page.Length < Page) break;
        }
        using (var c = run.Db.Open())
            c.Execute("UPDATE console SET refreshed_at = (SELECT created_at FROM task WHERE id = @Id) WHERE id = @Platform", new { run.Id, job.Platform });
        QueueFollowUps(run.Db);
    }

    /// <summary>
    /// F2.14 (#84): lo nuevo o cambiado en IGDB (`updated_at`) de cada consola habilitada desde su última carga, sin recargar
    /// todo. Una consola que nunca se cargó se carga entera. PC y PC VR siguen por saga. Al terminar cada consola se guarda
    /// hasta cuándo quedó al día.
    /// </summary>
    public static async Task Refresh(TaskRun run, CancellationToken ct)
    {
        var job = run.Data is null ? null : JsonSerializer.Deserialize<RefreshJob>(run.Data, JsonSerializerOptions.Web);
        if (job is null)
            using (var c = run.Db.Open())
                job = new RefreshJob(c.Query<string>("SELECT id FROM console WHERE enabled = 1 AND id NOT IN ('pc', 'pcvr') ORDER BY rowid").ToList(), 0,
                    DateTime.UtcNow.ToString("O"));
        var igdb = Client(run);
        var total = job.Consoles.Count;
        while (job.Consoles.Count > 0)
        {
            var console = job.Consoles[0];
            string? since;
            List<long> ids;
            using (var c = run.Db.Open())
            {
                since = c.ExecuteScalar<string?>("SELECT refreshed_at FROM console WHERE id = @console", new { console });
                ids = Platforms(c).Where(p => p.Value == console).Select(p => p.Key).ToList();
            }
            if (ids.Count > 0)
            {
                var after = since is null ? "" : $" & updated_at > {new DateTimeOffset(DateTime.Parse(since, null, System.Globalization.DateTimeStyles.AdjustToUniversal)).ToUnixTimeSeconds()}";
                var where = $"where platforms = ({string.Join(',', ids)}) & version_parent = null & game_type = ({string.Join(',', IgdbGame.PlayableTypes)}){after};";
                while (true)
                {
                    var page = await igdb.Query<IgdbGame>("games", $"{IgdbGame.Fields} {where} sort id asc; limit {Page}; offset {job.Offset};", ct);
                    await Import(run.Db, igdb, page, null, ct);
                    if (page.Length > 0) run.Log(ApiError.Of("tasks.log.refresh", ("platform", console), ("count", page.Length.ToString())));
                    job = job with { Offset = job.Offset + page.Length };
                    Report(run, total - job.Consoles.Count, total, ApiError.Of("tasks.igdb.refresh", ("platform", console),
                        ("done", (total - job.Consoles.Count).ToString()), ("total", total.ToString())), job);
                    if (page.Length < Page) break;
                }
            }
            using (var c = run.Db.Open())
                c.Execute("UPDATE console SET refreshed_at = @Started WHERE id = @console", new { job.Started, console });
            job = job with { Consoles = job.Consoles.Skip(1).ToList(), Offset = 0 };
            Report(run, total - job.Consoles.Count, total, ApiError.Of("tasks.igdb.refresh", ("platform", console),
                ("done", (total - job.Consoles.Count).ToString()), ("total", total.ToString())), job);
        }
        using (var c = run.Db.Open()) Claude.ClaudeBudget.Set(c, LastRefresh, job.Started);
        QueueFollowUps(run.Db);
    }

    /// <param name="Started">Cuándo empezó: lo que cambie en IGDB mientras corre entra en la próxima.</param>
    public record RefreshJob(List<string> Consoles, long Offset, string Started);

    public const string RefreshDays = "catalog.refreshDays", LastRefresh = "catalog.lastRefresh";

    /// <summary>Encola la actualización si pasaron los días configurados (7 por defecto) y no hay una pendiente. La lanza el horario de noche.</summary>
    public static long? RefreshIfDue(Database db, DateTime nowUtc)
    {
        using var c = db.Open();
        var days = c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @RefreshDays", new { RefreshDays }) is { } d && int.TryParse(d, out var n) ? n : 7;
        var last = c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @LastRefresh", new { LastRefresh });
        // Sin actualización previa, cuenta desde la última carga de una consola.
        last ??= c.ExecuteScalar<string?>("SELECT max(refreshed_at) FROM console");
        if (last is not null && DateTime.Parse(last, null, System.Globalization.DateTimeStyles.AdjustToUniversal) > nowUtc.AddDays(-days)) return null;
        return EnqueueRefresh(db);
    }

    /// <summary>«Actualizar ahora»: encola la actualización si no hay una pendiente.</summary>
    public static long EnqueueRefresh(Database db)
    {
        using (var c = db.Open())
            if (c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = 'igdb.refresh' AND status IN ('queued', 'running')") is { } id) return id;
        return TaskQueue.Enqueue(db, "igdb.refresh");
    }

    /// <summary>
    /// Los juegos de PC icónicos (#62, completa la #131): los más calificados en IGDB, triple A e indies conocidos.
    /// Cada uno entra con su saga asignada; la saga entera se carga con `--saga` o en F2.
    /// </summary>
    public static async Task Popular(TaskRun run, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<PopularJob>(run.Data!, JsonSerializerOptions.Web)!;
        var igdb = Client(run);
        var where = $"where platforms = (6) & version_parent = null & first_release_date != null & total_rating_count != null & game_type = ({string.Join(',', IgdbGame.PlayableTypes)});";
        while (job.Offset < PopularLimit)
        {
            var size = (int)Math.Min(Page, PopularLimit - job.Offset);
            var page = await igdb.Query<IgdbGame>("games", $"{IgdbGame.Fields} {where} sort total_rating_count desc; limit {size}; offset {job.Offset};", ct);
            await Import(run.Db, igdb, page, null, ct);
            job = job with { Offset = job.Offset + page.Length };
            Report(run, job.Offset, PopularLimit, ApiError.Of("tasks.igdb.popular", ("done", job.Offset.ToString()), ("total", PopularLimit.ToString())), job);
            if (page.Length < size) break;
        }
        QueueFollowUps(run.Db);
    }

    /// <summary>
    /// F2.17 (#89): completa la popularidad de los juegos cargados antes de guardarla, de a 500 por consulta. Sigue sola tras
    /// un reinicio: pide lo que aún no la tiene (los que IGDB no calificó quedan en 0).
    /// </summary>
    public static async Task Popularity(TaskRun run, CancellationToken ct)
    {
        var igdb = Client(run);
        long total, left;
        using (var c = run.Db.Open()) total = c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE igdb_id IS NOT NULL AND popularity IS NULL");
        do
        {
            List<long> ids;
            using (var c = run.Db.Open()) ids = c.Query<long>("SELECT igdb_id FROM game WHERE igdb_id IS NOT NULL AND popularity IS NULL LIMIT 500").ToList();
            if (ids.Count == 0) break;
            var rated = (await igdb.Query<Rated>("games", $"fields total_rating_count; where id = ({string.Join(',', ids)}); limit 500;", ct))
                .ToDictionary(r => r.Id, r => r.TotalRatingCount ?? 0);
            using (var c = run.Db.Open())
            {
                using var t = c.BeginTransaction();
                foreach (var id in ids)
                    c.Execute("UPDATE game SET popularity = @pop WHERE igdb_id = @id", new { id, pop = rated.GetValueOrDefault(id) }, t);
                t.Commit();
                left = c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE igdb_id IS NOT NULL AND popularity IS NULL");
            }
            run.Report((int)((total - left) * 100 / Math.Max(total, 1)), ApiError.Of("tasks.igdb.popularity",
                ("done", (total - left).ToString()), ("total", total.ToString())));
        } while (left > 0);
    }

    record Rated(long Id, long? TotalRatingCount);

    /// <summary>Al arrancar: si hay juegos sin popularidad y nada que la esté trayendo, encola la tarea (también reintenta tras un error).</summary>
    public static void EnqueuePopularity(Database db)
    {
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE igdb_id IS NOT NULL AND popularity IS NULL LIMIT 1") == 0) return;
        if (c.ExecuteScalar<long>("SELECT count(*) FROM task WHERE kind = 'igdb.popularity' AND status IN ('queued', 'running')") > 0) return;
        TaskQueue.Enqueue(db, "igdb.popularity");
    }

    record SteamLink(long Game, string Uid);

    /// <summary>Al terminar una carga: las portadas de lo nuevo y lo que completan Wikidata y RAWG (si no están ya esperando).</summary>
    static void QueueFollowUps(Database db)
    {
        bool Waiting(string kind)
        {
            using var c = db.Open();
            // También la que corre (#78): si no, cada carga encola otra que vuelve a consultar los juegos sin portada.
            return c.ExecuteScalar<long>("SELECT count(*) FROM task WHERE kind = @kind AND status IN ('queued', 'running')", new { kind }) > 0;
        }
        if (!Waiting("sgdb.images")) SteamGridDb.Enqueue(db);
        if (!Waiting("catalog.extra")) ExtraSources.Enqueue(db);
    }

    static void Report<T>(TaskRun run, long done, long total, ApiError detail, T job) =>
        run.Report(total == 0 ? 100 : (int)Math.Min(100, done * 100 / total), detail, JsonSerializer.Serialize(job, JsonSerializerOptions.Web));

    /// <summary>Busca la franquicia o colección que mejor coincide: primero el nombre exacto, después el más corto.</summary>
    static async Task<SagaRef?> FindSaga(Igdb igdb, string query, CancellationToken ct)
    {
        var q = query.Replace("\"", "").Replace("\\", "").Trim();
        var body = $"fields name, slug; where name ~ *\"{q}\"*; limit 50;";
        var franchises = await igdb.Query<IgdbNamed>("franchises", body, ct);
        var collections = await igdb.Query<IgdbNamed>("collections", body, ct);
        bool Exact(IgdbNamed n) => string.Equals(n.Name, q, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Slug, q, StringComparison.OrdinalIgnoreCase);
        IgdbNamed? Shortest(IgdbNamed[] list) => list.Where(n => n.Name is not null).MinBy(n => n.Name!.Length);

        if (franchises.FirstOrDefault(Exact) is { } f) return new("franchise", f.Id, f.Name);
        if (collections.FirstOrDefault(Exact) is { } c) return new("collection", c.Id, c.Name);
        if (Shortest(franchises) is { } fs) return new("franchise", fs.Id, fs.Name);
        return Shortest(collections) is { } cs ? new("collection", cs.Id, cs.Name) : null;
    }

    /// <summary>
    /// Guarda una página de juegos. Los remakes, remasters, ports y ediciones ampliadas pasan a ser versiones de su juego
    /// original (si no vinieron en la página, se piden), igual que un juego con el mismo nombre en la misma saga.
    /// Fuera quedan los que no tienen fecha (cancelados, rumores) salvo si son tuyos (`owned`), y al cargar una saga, sus
    /// invitados (#58). Lo corregido a mano no se toca: ver <see cref="FieldValues"/>.
    /// </summary>
    public static async Task Import(Database db, Igdb igdb, IEnumerable<IgdbGame> page, SagaRef? preferred, CancellationToken ct, bool owned = false)
    {
        var candidates = page.Where(g => g.VersionParent is null && IgdbGame.PlayableTypes.Contains(g.Type) && (owned || g.HasDate)).ToList();
        var games = candidates.Where(g => Belongs(g, preferred)).ToList();
        // Las ediciones siguen a su original: Hyrule Warriors Legends entra porque entra Hyrule Warriors.
        for (var more = Editions(); more.Count > 0; more = Editions()) games.AddRange(more);
        List<IgdbGame> Editions() => candidates.Where(g => !games.Contains(g) && g.EditionOf is { } p && games.Any(k => k.Id == p)).ToList();
        var missing = games.SelectMany(Children).Select(r => r.Id).Concat(games.Select(g => g.EditionOf).OfType<long>())
            .Except(games.Select(g => g.Id)).Distinct().ToList();
        foreach (var chunk in missing.Chunk(Page))
            games.AddRange(await igdb.Query<IgdbGame>("games", $"{IgdbGame.Fields} where id = ({string.Join(',', chunk)}); limit {Page};", ct));

        var parentOf = new Dictionary<long, (long Parent, string Kind)>();
        foreach (var g in games)
        {
            foreach (var (child, kind) in Children(g))
                parentOf.TryAdd(child, (g.Id, kind));
            if (g.EditionOf is { } parent && parent != g.Id) parentOf.TryAdd(g.Id, (parent, "original"));
        }

        using var c = db.Open();
        using var t = c.BeginTransaction();
        var byIgdb = new Dictionary<long, long>();
        // Primero los originales (y de ellos, los más antiguos), para que sus remakes y homónimos los encuentren.
        foreach (var g in games.OrderBy(g => parentOf.ContainsKey(g.Id)).ThenBy(g => g.FirstReleaseDate ?? long.MaxValue))
        {
            var kind = parentOf.TryGetValue(g.Id, out var p) ? p.Kind : "original";
            var root = g.Id;
            for (var i = 0; i < 10 && parentOf.TryGetValue(root, out var up); i++) root = up.Parent;

            long gameId;
            if (root != g.Id && byIgdb.TryGetValue(root, out var parentGame))
            {
                gameId = parentGame;
                MergeStandalone(c, t, g.Id, gameId, db);
            }
            else if (c.ExecuteScalar<long?>("SELECT game_id FROM version WHERE igdb_id = @Id", g, t) is { } existing
                     && c.ExecuteScalar<long?>("SELECT igdb_id FROM game WHERE id = @existing", new { existing }, t) != g.Id)
                gameId = existing; // ya es versión de otro juego (llegó antes con su original)
            else if (SameName(c, t, g, preferred) is { } namesake)
                gameId = namesake;
            else
            {
                gameId = UpsertGame(c, t, g, preferred);
                kind = g.Type switch { 8 => "remake", 9 => "remaster", _ => "original" };
            }
            byIgdb[g.Id] = gameId;
            UpsertVersions(c, t, g, gameId, kind);
        }
        t.Commit();
    }

    /// <summary>
    /// Al cargar una franquicia no entran los invitados: Smash Bros. o Mario Kart están en la franquicia Zelda porque Link
    /// aparece en ellos. Entra el juego que la tiene como principal (IGDB casi nunca la marca), como única, o que con varias
    /// está en una colección con el nombre de la saga (Hyrule Warriors está en Zelda y Musou, y en la colección Zelda).
    /// </summary>
    static bool Belongs(IgdbGame g, SagaRef? saga)
    {
        if (saga is not { Kind: "franchise" }) return true;
        if (g.Franchise is { } main) return main.Id == saga.IgdbId;
        if (g.Franchises is [{ } only]) return only.Id == saga.IgdbId;
        return saga.Name is { } name && (g.Collections ?? []).Any(c => c.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>Un juego nuevo con el mismo nombre que otro de su saga (A Link to the Past de SNES y de GBA) es una versión de ese.</summary>
    static long? SameName(SqliteConnection c, SqliteTransaction t, IgdbGame g, SagaRef? preferred)
    {
        if (c.ExecuteScalar<long?>("SELECT id FROM game WHERE igdb_id = @Id", g, t) is not null || SagaOf(g, preferred) is not { } saga) return null;
        var key = NameKey(g.Name);
        return c.Query<(long Id, string? Name)>("""
            SELECT g.id, g.original_name FROM game g JOIN saga s ON s.id = g.saga_id
            WHERE s.igdb_kind = @Kind AND s.igdb_id = @IgdbId ORDER BY g.year
            """, saga, t).FirstOrDefault(x => x.Name is not null && NameKey(x.Name) == key).Id is var id and not 0 ? id : null;
    }

    /// <summary>El nombre para comparar: minúsculas, solo letras y números.</summary>
    internal static string NameKey(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>Un solo formato de nombre: guiones largos como guion y sin espacios repetidos.</summary>
    static string Clean(string name) => System.Text.RegularExpressions.Regex.Replace(name.Replace('–', '-').Replace('—', '-'), @"\s+", " ").Trim();

    static IEnumerable<(long Id, string Kind)> Children(IgdbGame g) =>
        (g.Remakes ?? []).Select(id => (id, "remake"))
        .Concat((g.Remasters ?? []).Select(id => (id, "remaster")))
        .Concat((g.Ports ?? []).Select(id => (id, "original")));

    static long UpsertGame(SqliteConnection c, SqliteTransaction t, IgdbGame g, SagaRef? preferred)
    {
        var id = c.ExecuteScalar<long?>("SELECT id FROM game WHERE igdb_id = @Id", g, t)
                 ?? c.ExecuteScalar<long>("INSERT INTO game (igdb_id, name_es) VALUES (@Id, @Name) RETURNING id", g, t);
        FieldValues.Set(c, "game", id, "original_name", Clean(g.Name), Source, t);
        FieldValues.Set(c, "game", id, "name_es", Clean(SpanishName(g)), Source, t);
        FieldValues.Set(c, "game", id, "year", Year(g.FirstReleaseDate), Source, t);
        // Cuánta gente lo calificó en IGDB: la medida de «más reconocidas» (#89, decisión #166).
        c.Execute("UPDATE game SET popularity = @pop WHERE id = @id", new { id, pop = g.TotalRatingCount ?? 0 }, t);
        // La descripción (en inglés) no tiene columna: se guarda como dato y Claude la traduce una vez (F2.5).
        if (!string.IsNullOrWhiteSpace(g.Summary))
            c.Execute("""
                INSERT INTO field_value (entity, entity_id, field, value, source) VALUES ('game', @id, 'summary', @Summary, @Source)
                ON CONFLICT (entity, entity_id, field, source) DO UPDATE SET value = excluded.value
                """, new { id, g.Summary, Source }, t);

        // La saga se elige una vez; recargar otra saga no mueve el juego (Hyrule Warriors sigue en Zelda).
        if (c.ExecuteScalar<long?>("SELECT saga_id FROM game WHERE id = @id", new { id }, t) is null && SagaOf(g, preferred) is { } saga)
            c.Execute("UPDATE game SET saga_id = @saga WHERE id = @id", new { id, saga = UpsertSaga(c, t, saga) }, t);
        return id;
    }

    static SagaRef? SagaOf(IgdbGame g, SagaRef? preferred)
    {
        var franchises = g.Franchises ?? [];
        var collections = g.Collections ?? [];
        if (preferred is { Kind: "collection" } && collections.Any(n => n.Id == preferred.IgdbId)) return preferred;
        var franchise = preferred is { Kind: "franchise" } && franchises.Any(n => n.Id == preferred.IgdbId) ? preferred
            // La franquicia principal antes que las demás: Smash Bros. va a Smash, no a la primera de su lista.
            : (g.Franchise is { } main ? franchises.FirstOrDefault(n => n.Id == main.Id) ?? main : franchises.FirstOrDefault()) is { } f
                ? new SagaRef("franchise", f.Id, f.Name) : null;
        if (franchise is null) return collections.FirstOrDefault() is { } col ? new("collection", col.Id, col.Name) : null;
        // Sub-saga (decisión 153): Counter-Strike está en la franquicia Half-Life (nació como mod), pero tiene su colección
        // propia, sin «Half-Life» en el nombre: va a la saga Counter-Strike. Hyrule Warriors sigue en Zelda (está en la colección Zelda).
        if (franchise.Name is { } name && collections.Length > 0 && !collections.Any(n => n.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
            && collections.FirstOrDefault(n => n.Name is not null) is { } own)
            return new("collection", own.Id, own.Name);
        return franchise;
    }

    static long UpsertSaga(SqliteConnection c, SqliteTransaction t, SagaRef s)
    {
        var id = c.ExecuteScalar<long?>("SELECT id FROM saga WHERE igdb_kind = @Kind AND igdb_id = @IgdbId", s, t)
                 ?? c.ExecuteScalar<long>("INSERT INTO saga (name, igdb_kind, igdb_id) VALUES (@name, @Kind, @IgdbId) RETURNING id",
                     new { name = s.Name ?? $"IGDB {s.IgdbId}", s.Kind, s.IgdbId }, t);
        if (s.Name is not null) FieldValues.Set(c, "saga", id, "name", s.Name, Source, t);
        return id;
    }

    /// <summary>Un remake que antes se cargó como juego propio pasa a su original (si no tiene seguimiento).</summary>
    static void MergeStandalone(SqliteConnection c, SqliteTransaction t, long igdbId, long target, Database db)
    {
        if (c.ExecuteScalar<long?>("SELECT id FROM game WHERE igdb_id = @igdbId AND id <> @target", new { igdbId, target }, t) is not { } old) return;
        c.Execute("UPDATE version SET game_id = @target WHERE game_id = @old", new { target, old }, t);
        // Su seguimiento pasa al original si este no tiene uno (#59); si los dos tienen, el suelto se queda para no perder nada.
        c.Execute("UPDATE progress SET game_id = @target WHERE game_id = @old AND NOT EXISTS (SELECT 1 FROM progress WHERE game_id = @target)", new { target, old }, t);
        if (c.ExecuteScalar<long>("SELECT count(*) FROM progress WHERE game_id = @old", new { old }, t) > 0) return;
        c.Execute("DELETE FROM field_value WHERE entity = 'game' AND entity_id = @old; DELETE FROM game WHERE id = @old", new { old }, t);
        // SQLite puede volver a usar ese id: sus imágenes (caché, se bajan de nuevo) no deben quedar para otro juego (#58).
        SteamGridDb.Forget(db, old);
    }

    static void UpsertVersions(SqliteConnection c, SqliteTransaction t, IgdbGame g, long gameId, string kind)
    {
        var platforms = Platforms(c, t);
        foreach (var platform in (g.Platforms ?? []).Where(platforms.ContainsKey).GroupBy(p => platforms[p]))
        {
            var dates = (g.ReleaseDates ?? []).Where(d => d.Platform is { } p && platform.Contains(p) && d.Date is not null).Select(d => d.Date!.Value).ToList();
            var year = Year(dates.Count > 0 ? dates.Min() : g.FirstReleaseDate);
            var steam = PcPlatforms.Contains(platform.Key) ? SteamId(g) : null;
            // Al recargar solo cambian el año y el id de Steam: el tipo y el juego pueden estar corregidos a mano.
            c.Execute("""
                INSERT INTO version (game_id, platform, kind, year, igdb_id, steam_app_id) VALUES (@gameId, @platform, @kind, @year, @igdbId, @steam)
                ON CONFLICT (igdb_id, platform) DO UPDATE SET year = excluded.year, steam_app_id = COALESCE(excluded.steam_app_id, version.steam_app_id)
                """, new { gameId, platform = platform.Key, kind, year, igdbId = g.Id, steam }, t);
        }
    }

    /// <summary>El nombre en español si IGDB lo tiene como nombre alternativo; si no, el original.</summary>
    static string SpanishName(IgdbGame g) =>
        (g.AlternativeNames ?? []).FirstOrDefault(n => n.Name is not null && n.Comment is { } note
            && (note.Contains("spanish", StringComparison.OrdinalIgnoreCase) || note.Contains("español", StringComparison.OrdinalIgnoreCase)))?.Name
        ?? g.Name;

    static int? Year(long? unix) => unix is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).Year : null;

    /// <summary>Fuente 1 de IGDB = Steam (antes `category`, ahora `external_game_source`).</summary>
    static long? SteamId(IgdbGame g) =>
        (g.ExternalGames ?? []).FirstOrDefault(e => (e.ExternalGameSource ?? e.Category) == 1 && long.TryParse(e.Uid, out _)) is { } e ? long.Parse(e.Uid!) : null;
}

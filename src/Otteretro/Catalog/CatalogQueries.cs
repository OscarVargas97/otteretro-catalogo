using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="Id">0 = «Emulación», los juegos sin saga (§6.2).</param>
/// <param name="Exclusive">La empresa de la que es exclusiva: todas sus versiones de consola son de ella; PC no cuenta (#165).</param>
/// <param name="Popularity">Calificaciones en IGDB de sus juegos (en la consola filtrada, si hay): «más reconocidas» (#166).</param>
public record SagaCard(long Id, string Name, bool Favorite, bool Following, bool Iconic, string[] Platforms, int Games, int Owned,
    long? CoverGameId, long Playtime, string? LastPlayed, string? Exclusive, long Popularity);

/// <param name="Stores">Tiendas donde lo tienes (por ahora, steam).</param>
/// <param name="Playtime">Minutos jugados según las tiendas.</param>
public record GameCard(long Id, string Name, int? Year, string[] Platforms, bool Owned, string? Status, long Playtime, bool HasCover,
    long? SagaId, string? SagaName, string[] Stores);

/// <param name="HeroGameId">De qué juego sale la imagen de la cabecera (#178): el icónico más reciente con héroe; si no hay, el más reciente con héroe.</param>
public record SagaDetail(SagaCard Saga, List<GameCard> Games, long? HeroGameId);
/// <param name="WithPc">Juegos de la consola que también tienen versión de PC (el filtro «Versión PC» de Colección).</param>
/// <param name="HasDat">Tiene DAT de No-Intro o Redump: se puede verificar por hash (#179).</param>
public record ConsoleCount(string Id, string Name, string? Company, int Games, int Owned, int WithPc, bool HasDat);

/// <param name="Unlinked">Lo que tienes en una tienda y todavía no está en el catálogo (solo en PC): aparece igual.</param>
/// <param name="Total">Cuántos juegos hay con ese filtro; <paramref name="Games"/> trae solo el tramo pedido (#177).</param>
public record CollectionPage(List<GameCard> Games, List<StoreEntry> Unlinked, int Total);

/// <summary>Un tramo de Sagas, ya filtradas y ordenadas (#177).</summary>
public record SagaPage(List<SagaCard> Items, int Total);

/// <summary>Un tramo de los juegos de una saga (#177).</summary>
public record GamePage(List<GameCard> Items, int Total);
public record StoreEntry(string Store, string? Name, long? Playtime, string? LastPlayed);
/// <param name="Path">Ruta completa (en su última ubicación conocida).</param>
/// <param name="Component">BASE, UPD o DLC (Switch); null en lo demás.</param>
/// <param name="Verified">Su SHA-1 coincide con el DAT (#179).</param>
/// <param name="Executable">PC (#182): el ejecutable principal, relativo a la carpeta; <paramref name="Executables"/>, los que hay para elegir.</param>
public record FileEntry(long Id, string Path, string? Component, long? Size, bool Verified, string? Executable = null, List<string>? Executables = null);
public record VersionRow(long Id, string Platform, string Kind, int? Year, List<StoreEntry> Stores, List<FileEntry> Files);

/// <param name="Winner">Es el valor que se muestra (lo manual siempre gana).</param>
public record FieldRow(string Field, string? Value, string Source, bool Manual, bool Winner);
public record ProgressInfo(string Status, string? StartedAt, string? FinishedAt, int? Rating, string? Notes, bool Favorite);
public record GameDetail(long Id, string NameEs, string? OriginalName, int? Year, long? SagaId, string? SagaName, bool HasCover, bool HasHero,
    List<VersionRow> Versions, List<FieldRow> Fields, ProgressInfo Progress, long Playtime, string? LastPlayed);
public record SearchResults(List<SagaCard> Sagas, List<GameCard> Games);

/// <summary>Lo que leen las pantallas del catálogo (F1.9). Las listas son de una persona: se arman en memoria.</summary>
public static class CatalogQueries
{
    public static readonly string[] Statuses = ["pending", "playing", "finished", "completed", "dropped"];

    /// <summary>Campos de un juego que se pueden corregir a mano desde la ficha.</summary>
    public static readonly string[] EditableFields = ["name_es", "original_name", "year"];

    // Clases con propiedades (no registros posicionales): Dapper las llena por nombre aunque una columna venga vacía.
    sealed class GameRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public long? Year { get; init; }
        public long? SagaId { get; init; }
        public string? SagaName { get; init; }
        public string? Platforms { get; init; }
        public long Owned { get; init; }
        public string? Status { get; init; }
        public long Playtime { get; init; }
        public string? Stores { get; init; }
    }

    sealed class SagaAgg
    {
        public long SagaId { get; init; }
        public long Owned { get; init; }
        public long Playtime { get; init; }
        public string? LastPlayed { get; init; }
    }

    sealed class SagaRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public long Favorite { get; init; }
        public long Following { get; init; }
        public long Iconic { get; init; }
    }

    const string GameSelect = """
        SELECT g.id, g.name_es AS Name, g.year, g.saga_id AS SagaId, s.name AS SagaName,
               (SELECT group_concat(DISTINCT v.platform) FROM version v WHERE v.game_id = g.id) AS Platforms,
               EXISTS (SELECT 1 FROM source o JOIN version v ON v.id = o.version_id WHERE v.game_id = g.id) AS Owned,
               p.status,
               (SELECT coalesce(sum(o.playtime), 0) FROM source o JOIN version v ON v.id = o.version_id WHERE v.game_id = g.id) AS Playtime,
               (SELECT group_concat(DISTINCT o.store) FROM source o JOIN version v ON v.id = o.version_id WHERE v.game_id = g.id AND o.kind = 'store') AS Stores
        FROM game g LEFT JOIN saga s ON s.id = g.saga_id LEFT JOIN progress p ON p.game_id = g.id
        """;

    /// <summary>Orden de las consolas (el de la semilla), para mostrar las plataformas siempre igual. Se lee una vez por consulta.</summary>
    static Dictionary<string, int> ConsoleOrder(SqliteConnection c) =>
        c.Query<string>("SELECT id FROM console ORDER BY rowid").Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

    static string[] Ordered(Dictionary<string, int> order, string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : csv.Split(',').OrderBy(p => order.GetValueOrDefault(p, int.MaxValue)).ToArray();

    /// <summary>
    /// Los juegos con portada. Recorrer la carpeta (25.000 archivos) costaba ~100 ms por pedido (#106): se guarda y se vuelve a
    /// leer solo cuando cambia la fecha de la carpeta, que cambia al agregar o borrar un archivo.
    /// </summary>
    internal static HashSet<long> Covers(Database db) => Images(db, "cover");

    /// <summary>Los juegos con una imagen de ese tipo (cover, hero o logo), recordados como <see cref="Covers"/>.</summary>
    internal static HashSet<long> Images(Database db, string kind)
    {
        var folder = SteamGridDb.Folder(db);
        if (!Directory.Exists(folder)) return [];
        var stamp = Directory.GetLastWriteTimeUtc(folder);
        lock (imageCache)
            if (imageCache.TryGetValue(kind, out var hit) && hit.Folder == folder && hit.Stamp == stamp) return hit.Ids;
        var ids = Directory.EnumerateFiles(folder, $"*-{kind}.*")
            .Select(f => long.TryParse(Path.GetFileName(f).Split('-')[0], out var id) ? id : 0).ToHashSet();
        lock (imageCache) imageCache[kind] = (folder, stamp, ids);
        return ids;
    }

    static readonly Dictionary<string, (string Folder, DateTime Stamp, HashSet<long> Ids)> imageCache = [];

    /// <summary>Lo que muestra un tramo de las listas de juegos (Colección, una saga, la búsqueda).</summary>
    public const int PageSize = 60;

    /// <param name="where">Filtro, orden y tramo (LIMIT) sobre `game g` y `saga s`.</param>
    static List<GameCard> Games(Database db, SqliteConnection c, string where, object? args)
    {
        // Primero los ids del tramo y después sus datos (#106): las subconsultas de GameSelect se calculaban para todos los
        // juegos que cumplen el filtro (20.000 en PC), no solo para los que se muestran.
        var ids = c.Query<long>($"SELECT g.id FROM game g LEFT JOIN saga s ON s.id = g.saga_id {where}", args).ToList();
        if (ids.Count == 0) return [];
        var covers = Covers(db);
        var order = ConsoleOrder(c);
        var rows = c.Query<GameRow>($"{GameSelect} WHERE g.id IN (SELECT value FROM json_each(@ids))", new { ids = JsonSerializer.Serialize(ids) })
            .ToDictionary(r => r.Id);
        return ids.Select(id => rows[id])
            .Select(r => new GameCard(r.Id, r.Name, (int?)r.Year, Ordered(order, r.Platforms), r.Owned != 0, r.Status, r.Playtime,
                covers.Contains(r.Id), r.SagaId, r.SagaName, r.Stores?.Split(',') ?? []))
            .ToList();
    }

    /// <param name="filter">favorite, following, iconic, exclusive, owned, missing o pc.</param>
    /// <param name="sort">name, recognized, played o activity.</param>
    /// <param name="only">Solo esa saga (la ficha de una saga, F2.19): no arma las demás.</param>
    public static List<SagaCard> Sagas(Database db, string? filter = null, string? platform = null, string? company = null, string? sort = null,
        long? only = null, long[]? ids = null)
    {
        using var c = db.Open();
        // Sin coalesce en el WHERE: así usa el índice.
        var one = (only, ids) switch { (0, _) => "g.saga_id IS NULL", (not null, _) => "g.saga_id = @only", (_, not null) => "g.saga_id IN @ids", _ => "" };
        var args = new { only, ids, platform };
        // Por partes (#106): un solo JOIN de juegos × versiones × lo tuyo, agrupado, tardaba 150 ms con el catálogo real; así, ~70.
        var games = c.Query<(long SagaId, long Games, long Popularity)>(
            $"SELECT coalesce(g.saga_id, 0), count(*), coalesce(sum(g.popularity), 0) FROM game g {Where(one)} GROUP BY coalesce(g.saga_id, 0)", args)
            .ToDictionary(x => x.SagaId);
        var platformsOf = c.Query<(long SagaId, string Platform)>(
            $"SELECT DISTINCT coalesce(g.saga_id, 0), v.platform FROM version v JOIN game g ON g.id = v.game_id {Where(one)}", args)
            .GroupBy(x => x.SagaId).ToDictionary(x => x.Key, x => string.Join(',', x.Select(p => p.Platform)));
        var mine = c.Query<SagaAgg>($"""
            SELECT coalesce(g.saga_id, 0) AS SagaId, count(DISTINCT g.id) AS Owned, coalesce(sum(o.playtime), 0) AS Playtime, max(o.last_played) AS LastPlayed
            FROM source o JOIN version v ON v.id = o.version_id JOIN game g ON g.id = v.game_id {Where(one)}
            GROUP BY coalesce(g.saga_id, 0)
            """, args).ToDictionary(a => a.SagaId);
        // Con una consola elegida, la popularidad cuenta solo sus juegos (#166).
        var popularity = string.IsNullOrEmpty(platform)
            ? games.ToDictionary(x => x.Key, x => x.Value.Popularity)
            : c.Query<(long SagaId, long Popularity)>($"""
                SELECT coalesce(g.saga_id, 0), coalesce(sum(g.popularity), 0) FROM game g
                {Where(one, "EXISTS (SELECT 1 FROM version v WHERE v.game_id = g.id AND v.platform = @platform)")}
                GROUP BY coalesce(g.saga_id, 0)
                """, args).ToDictionary(p => p.SagaId, p => p.Popularity);

        var rows = c.Query<SagaRow>($"""
            SELECT s.id, s.name, coalesce(u.favorite, 0) AS Favorite, coalesce(u.following, 0) AS Following, s.iconic
            FROM saga s LEFT JOIN saga_user u ON u.saga_id = s.id {(only is not null ? "WHERE s.id = @only" : ids is not null ? "WHERE s.id IN @ids" : "")}
            """, args).ToList();
        if (games.ContainsKey(0)) rows.Add(new SagaRow());
        var companies = c.Query<(string Id, string? Company)>("SELECT id, company FROM console").ToDictionary(x => x.Id, x => x.Company);
        var order = ConsoleOrder(c);

        var cards = rows.Select(s =>
        {
            var a = mine.GetValueOrDefault(s.Id) ?? new SagaAgg { SagaId = s.Id };
            var platforms = Ordered(order, platformsOf.GetValueOrDefault(s.Id));
            return new SagaCard(s.Id, s.Name, s.Favorite != 0, s.Following != 0, s.Iconic != 0, platforms, (int)games.GetValueOrDefault(s.Id).Games, (int)a.Owned,
                null, a.Playtime, a.LastPlayed,
                s.Id == 0 ? null : ExclusiveOf(platforms, companies), popularity.GetValueOrDefault(s.Id));
        });
        cards = filter switch
        {
            "favorite" => cards.Where(s => s.Favorite),
            "following" => cards.Where(s => s.Following),
            "iconic" => cards.Where(s => s.Iconic),
            "exclusive" => cards.Where(s => s.Exclusive is not null && (string.IsNullOrEmpty(company) || s.Exclusive == company)),
            "owned" => cards.Where(s => s.Owned > 0),
            "missing" => cards.Where(s => s.Owned < s.Games),
            "pc" => cards.Where(s => s.Platforms.Contains("pc")),
            _ => cards,
        };
        if (!string.IsNullOrEmpty(platform)) cards = cards.Where(s => s.Platforms.Contains(platform));
        if (!string.IsNullOrEmpty(company)) cards = cards.Where(s => s.Platforms.Any(p => companies.GetValueOrDefault(p) == company));
        var byName = cards.OrderBy(s => s.Id == 0).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
        var sorted = (sort switch
        {
            // Primero lo icónico que aprobaste, después lo más calificado en IGDB (#166).
            "recognized" => cards.OrderBy(s => s.Id == 0).ThenByDescending(s => s.Iconic).ThenByDescending(s => s.Popularity)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
            "played" => cards.OrderBy(s => s.Id == 0).ThenByDescending(s => s.Playtime).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
            "activity" => cards.OrderBy(s => s.Id == 0).ThenByDescending(s => s.LastPlayed ?? "").ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => byName,
        }).ToList();
        return only is not null || ids is not null ? WithCovers(db, c, sorted) : sorted;
    }

    /// <summary>Un tramo de Sagas (#177): se filtran y ordenan todas, pero solo el tramo lleva portada y viaja a la pantalla.</summary>
    public static SagaPage SagasPage(Database db, string? filter, string? platform, string? company, string? sort, int offset, int limit)
    {
        var all = Sagas(db, filter, platform, company, sort);
        using var c = db.Open();
        return new SagaPage(WithCovers(db, c, all.Skip(offset).Take(limit).ToList()), all.Count);
    }

    /// <summary>La portada de cada saga: la del primer juego (por año) que la tenga. Solo de las que se muestran.</summary>
    static List<SagaCard> WithCovers(Database db, SqliteConnection c, List<SagaCard> cards)
    {
        if (cards.Count == 0) return cards;
        var covers = Covers(db);
        var ids = JsonSerializer.Serialize(cards.Select(s => s.Id));
        var coverOf = c.Query<(long SagaId, long Id, long? Year)>("""
            SELECT coalesce(g.saga_id, 0), g.id, g.year FROM game g
            WHERE g.saga_id IN (SELECT value FROM json_each(@ids)) OR (g.saga_id IS NULL AND 0 IN (SELECT value FROM json_each(@ids)))
            """, new { ids })
            .Where(g => covers.Contains(g.Id)).GroupBy(g => g.SagaId)
            .ToDictionary(g => g.Key, g => g.MinBy(x => (x.Year is null, x.Year ?? 0, x.Id)).Id);
        return cards.Select(s => s with { CoverGameId = coverOf.TryGetValue(s.Id, out var cover) ? cover : null }).ToList();
    }

    static string Where(params string[] conditions) =>
        conditions.Where(x => x != "").ToArray() is { Length: > 0 } all ? "WHERE " + string.Join(" AND ", all) : "";

    /// <summary>La empresa de la que es exclusiva una saga: todas sus consolas son de una sola; PC y PC VR no cuentan (#165).</summary>
    internal static string? ExclusiveOf(IEnumerable<string> platforms, Dictionary<string, string?> companies)
    {
        var owners = platforms.Where(p => p is not ("pc" or "pcvr")).Select(p => companies.GetValueOrDefault(p)).Distinct().ToList();
        return owners is [{ } only] ? only : null;
    }

    public static SagaDetail? Saga(Database db, long id)
    {
        var card = Sagas(db, only: id).FirstOrDefault();
        if (card is null) return null;
        using var c = db.Open();
        var heroes = Images(db, "hero");
        var hero = id == 0 ? null : c.Query<long>("SELECT id FROM game WHERE saga_id = @id ORDER BY iconic DESC, year IS NULL, year DESC, id DESC", new { id })
            .Cast<long?>().FirstOrDefault(g => heroes.Contains(g!.Value));
        return new SagaDetail(card, SagaGames(db, c, id, 0, PageSize), hero);
    }

    /// <summary>Un tramo de los juegos de una saga, por año (#177): «Emulación» tiene 30.000.</summary>
    public static GamePage SagaGames(Database db, long id, int offset, int limit)
    {
        using var c = db.Open();
        var total = c.ExecuteScalar<int>(id == 0 ? "SELECT count(*) FROM game WHERE saga_id IS NULL" : "SELECT count(*) FROM game WHERE saga_id = @id", new { id });
        return new GamePage(SagaGames(db, c, id, offset, limit), total);
    }

    static List<GameCard> SagaGames(Database db, SqliteConnection c, long id, int offset, int limit) =>
        Games(db, c, $"{(id == 0 ? "WHERE g.saga_id IS NULL" : "WHERE g.saga_id = @id")} ORDER BY g.year IS NULL, g.year, g.name_es LIMIT @limit OFFSET @offset",
            new { id, offset, limit });

    public static bool SetSagaFlags(Database db, long id, bool? favorite, bool? following)
    {
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT count(*) FROM saga WHERE id = @id", new { id }) == 0) return false;
        c.Execute("""
            INSERT INTO saga_user (saga_id, favorite, following) VALUES (@id, coalesce(@favorite, 0), coalesce(@following, 0))
            ON CONFLICT (saga_id) DO UPDATE SET favorite = coalesce(@favorite, favorite), following = coalesce(@following, following)
            """, new { id, favorite, following });
        return true;
    }

    public static List<ConsoleCount> Consoles(Database db)
    {
        using var c = db.Open();
        // Los mismos números que los filtros de Colección (F2.18): lo que tienes de esa consola y lo que además sale en PC.
        return c.Query<(string Id, string Name, string? Company, long Games, long Owned, long WithPc)>("""
            SELECT c.id, c.name, c.company, count(DISTINCT v.game_id), count(DISTINCT CASE WHEN o.id IS NOT NULL THEN v.game_id END),
                   count(DISTINCT CASE WHEN EXISTS (SELECT 1 FROM version p WHERE p.game_id = v.game_id AND p.platform = 'pc') THEN v.game_id END)
            FROM console c LEFT JOIN version v ON v.platform = c.id LEFT JOIN source o ON o.version_id = v.id
            GROUP BY c.id ORDER BY c.rowid
            """).Select(r => new ConsoleCount(r.Id, r.Name, r.Company, (int)r.Games, (int)r.Owned, (int)r.WithPc, false)).ToList();
    }

    /// <param name="filter">owned, missing o pc (tiene versión de PC).</param>
    /// <param name="sort">saga, name o year.</param>
    public static CollectionPage Collection(Database db, string platform, string? filter = null, string? sort = null, int offset = 0, int limit = PageSize)
    {
        using var c = db.Open();
        var where = """
            WHERE EXISTS (SELECT 1 FROM version v WHERE v.game_id = g.id AND v.platform = @platform)
            """;
        where += filter switch
        {
            "owned" => " AND EXISTS (SELECT 1 FROM version v JOIN source o ON o.version_id = v.id WHERE v.game_id = g.id AND v.platform = @platform)",
            "missing" => " AND NOT EXISTS (SELECT 1 FROM version v JOIN source o ON o.version_id = v.id WHERE v.game_id = g.id AND v.platform = @platform)",
            "pc" => " AND EXISTS (SELECT 1 FROM version v WHERE v.game_id = g.id AND v.platform = 'pc')",
            _ => "",
        };
        var total = c.ExecuteScalar<int>($"SELECT count(*) FROM game g {where}", new { platform });
        where += sort switch
        {
            "name" => " ORDER BY g.name_es",
            "year" => " ORDER BY g.year IS NULL, g.year, g.name_es",
            _ => " ORDER BY s.name IS NULL, s.name, g.year IS NULL, g.year, g.name_es",
        };
        var games = Games(db, c, where + " LIMIT @limit OFFSET @offset", new { platform, limit, offset });
        // Lo de Steam que IGDB no tiene va una sola vez, con el primer tramo.
        var unlinked = platform == "pc" && filter != "missing" && offset == 0
            ? c.Query<StoreEntry>("SELECT store, name, playtime, last_played AS LastPlayed FROM source WHERE kind = 'store' AND version_id IS NULL ORDER BY name").ToList()
            : [];
        return new CollectionPage(games, unlinked, total);
    }

    public static GameDetail? Game(Database db, long id)
    {
        using var c = db.Open();
        var g = c.QuerySingleOrDefault<(long Id, string NameEs, string? OriginalName, long? Year, long? SagaId, string? SagaName)>(
            "SELECT g.id, g.name_es, g.original_name, g.year, g.saga_id, s.name FROM game g LEFT JOIN saga s ON s.id = g.saga_id WHERE g.id = @id", new { id });
        if (g.Id == 0) return null;

        var stores = c.Query<(long VersionId, string Store, string? Name, long? Playtime, string? LastPlayed)>("""
            SELECT o.version_id, o.store, o.name, o.playtime, o.last_played FROM source o JOIN version v ON v.id = o.version_id
            WHERE v.game_id = @id AND o.kind = 'store'
            """, new { id }).ToList();
        var files = c.Query<(long Id, long VersionId, string Root, string Relative, string? Component, long? Size, bool Verified, string? Executable)>("""
            SELECT o.id, o.version_id, l.path, o.relative_path, o.component, o.size, o.verified, o.executable FROM source o
            JOIN version v ON v.id = o.version_id JOIN library l ON l.id = o.library_id
            WHERE v.game_id = @id AND o.kind = 'file' ORDER BY o.relative_path
            """, new { id }).ToList();
        var order = c.Query<string>("SELECT id FROM console ORDER BY rowid").ToList();
        var versions = c.Query<(long Id, string Platform, string Kind, long? Year)>("SELECT id, platform, kind, year FROM version WHERE game_id = @id", new { id })
            .OrderBy(v => order.IndexOf(v.Platform)).ThenBy(v => v.Year)
            .Select(v => new VersionRow(v.Id, v.Platform, v.Kind, (int?)v.Year,
                stores.Where(s => s.VersionId == v.Id).Select(s => new StoreEntry(s.Store, s.Name, s.Playtime, s.LastPlayed)).ToList(),
                files.Where(f => f.VersionId == v.Id).Select(f => new FileEntry(f.Id, Path.Combine(f.Root, f.Relative), f.Component, f.Size, f.Verified,
                    f.Executable, null)).ToList()))
            .ToList();

        var fields = c.Query<(string Field, string? Value, string Source, bool Manual)>(
                "SELECT field, value, source, manual FROM field_value WHERE entity = 'game' AND entity_id = @id", new { id })
            .GroupBy(f => f.Field)
            .SelectMany(group => group.OrderBy(f => f.Value is null && !f.Manual).ThenBy(f => FieldValues.Rank(f.Field, f.Source, f.Manual)).Select((f, i) => new FieldRow(f.Field, f.Value, f.Source, f.Manual, i == 0)))
            .ToList();

        var p = c.QuerySingleOrDefault<(string Status, string? StartedAt, string? FinishedAt, long? Rating, string? Notes, long Favorite)?>(
            "SELECT status, started_at, finished_at, rating, notes, favorite FROM progress WHERE game_id = @id", new { id });
        var progress = p is { } x ? new ProgressInfo(x.Status, x.StartedAt, x.FinishedAt, (int?)x.Rating, x.Notes, x.Favorite != 0)
            : new ProgressInfo("pending", null, null, null, null, false);

        return new GameDetail(g.Id, g.NameEs, g.OriginalName, (int?)g.Year, g.SagaId, g.SagaName,
            SteamGridDb.Find(db, id, "cover") is not null, SteamGridDb.Find(db, id, "hero") is not null,
            versions, fields, progress, stores.Sum(s => s.Playtime ?? 0), stores.Max(s => s.LastPlayed));
    }

    public static bool SaveProgress(Database db, long id, ProgressInfo p)
    {
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE id = @id", new { id }) == 0) return false;
        c.Execute("""
            INSERT INTO progress (game_id, status, started_at, finished_at, rating, notes, favorite)
            VALUES (@id, @Status, @StartedAt, @FinishedAt, @Rating, @Notes, @Favorite)
            ON CONFLICT (game_id) DO UPDATE SET status = excluded.status, started_at = excluded.started_at, finished_at = excluded.finished_at,
                rating = excluded.rating, notes = excluded.notes, favorite = excluded.favorite
            """, new { id, p.Status, p.StartedAt, p.FinishedAt, p.Rating, p.Notes, p.Favorite });
        return true;
    }

    /// <summary>Busca sagas y juegos por nombre (en español u original). Sin distinguir mayúsculas.</summary>
    public static SearchResults Search(Database db, string q)
    {
        var like = $"%{q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        using var c = db.Open();
        // Solo las sagas que coinciden (#106): armar todas para filtrar después costaba lo mismo que la pantalla Sagas.
        var ids = c.Query<long>(@"SELECT id FROM saga WHERE name LIKE @like ESCAPE '\'", new { like }).ToArray();
        var sagas = ids.Length == 0 ? [] : Sagas(db, ids: ids).Where(s => s.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(8).ToList();
        var games = Games(db, c, """
            WHERE g.name_es LIKE @like ESCAPE '\' OR g.original_name LIKE @like ESCAPE '\'
            ORDER BY g.name_es LIKE @start ESCAPE '\' DESC, g.year IS NULL, g.year LIMIT 20
            """, new { like, start = like[1..] });
        return new SearchResults(sagas, games);
    }
}

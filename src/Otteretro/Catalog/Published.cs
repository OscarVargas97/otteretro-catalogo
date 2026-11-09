using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Otteretro.Data;

namespace Otteretro.Catalog;

public record PublishedField(string Field, string? Value, string Source, bool Manual);
public record PublishedCriterion(string Criterion, string? Value, string? Text);
/// <param name="Codes">Su TitleID o serial (#46): con ellos Otteretro la reconoce por ID. Null si no tiene.</param>
public record PublishedVersion(long Id, string Platform, string Kind, int? Year, long? IgdbId, long? SteamAppId, List<PublishedCriterion> Criteria,
    List<string>? Codes = null);
/// <summary>Un port nativo de PC del juego (#69): lo que Otteretro muestra en Novedades y ofrece instalar.</summary>
/// <param name="Platform">La consola de la que sale el juego.</param>
/// <param name="Kind">decomp, recomp, remake, port, native-layer o engine.</param>
/// <param name="Status">complete (100 % jugable), playable (jugable con fallos), beta, experimental o no-port (hay decompilación, todavía sin port: no se instala).</param>
/// <param name="FullyPlayable">True si es 100 % jugable (Status complete): Otteretro lo dice tal cual, sin interpretar el estado.</param>
/// <param name="Added">Cuándo entró al catálogo (AAAA-MM-DD).</param>
/// <param name="Delisted">Su fuente ya no lo lista (se conserva); null si sigue listado.</param>
/// <param name="EmulatorLevel">Cómo corre el juego en <paramref name="Emulator"/>: 3 perfecto, 2 jugable, 1 parcial, 0 no funciona; null sin dato.</param>
public record PublishedPort(long Id, string Platform, string Project, string Kind, string? Toolchain, string Status, bool FullyPlayable, bool Abandoned, string Link,
    string Source, string Added, string? Emulator, int? EmulatorLevel, bool? Delisted = null);
/// <param name="Images">De dónde se baja cada imagen (#6): cover, hero o logo → dirección en SteamGridDB o IGDB.</param>
/// <param name="Ports">Sus ports (#69); null si no tiene.</param>
public record PublishedGame(long Id, string NameEs, string? OriginalName, int? Year, long? IgdbId, long? Popularity, bool Iconic, List<PublishedField> Fields, List<PublishedVersion> Versions,
    Dictionary<string, string>? Images = null, List<PublishedPort>? Ports = null);
public record PublishedStep(int Position, long GameId, long? RecommendedVersionId, string? Reason);
public record PublishedRoute(long Id, string Kind, string? Rationale, string? Sources, bool Approved, List<PublishedStep> Steps);
public record PublishedSaga(long Id, string Name, string? IgdbKind, long? IgdbId, bool Iconic, List<PublishedField> Fields, List<PublishedGame> Games, List<PublishedRoute> Routes);
public record PublishedLooseGames(List<PublishedGame> Games);
public record PublishedConsole(string Id, string Name, string? Company, string? EmulationStatus, List<long> IgdbPlatforms);
/// <param name="Kind">saga, game o version.</param>
/// <param name="NewId">Con qué se reemplazó; null si se quitó sin reemplazo.</param>
public record PublishedReplacement(string Kind, long OldId, long? NewId);
/// <param name="Sha256">Lo que verifica la app antes de usar la base.</param>
/// <param name="Content">Hash de los datos, fila por fila y en orden (#44): igual si los datos son iguales, aunque el archivo comprimido no lo sea.</param>
public record PublishedManifest(string Sha256, int Sagas, int Games, int Versions, string? Content = null, string[]? Schema = null);

/// <summary>
/// El catálogo publicado (#3, decisiones 253 y 254): un JSON por saga, que se puede revisar en un PR, y la base compilada que baja la app. Los ids son permanentes:
/// lo fusionado o quitado queda en `replaced.json`, para que la app pase lo de cada persona al juego que lo reemplaza.
/// </summary>
public static partial class Published
{
    const string SagasFolder = "sagas", LooseFolder = "sin-saga", ConsolesFile = "consolas.json", ReplacedFile = "reemplazados.json";
    /// <summary>Los juegos sin saga son unos 30.000: de a mil por archivo, para que un cambio no sea un archivo gigante.</summary>
    const int LoosePerFile = 1000;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static void Save(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, value.GetType(), Json).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
    }

    static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;

    /// <summary>Escribe el catálogo como archivos. Mismo catálogo, mismos bytes: en un PR solo se ve lo que cambió.</summary>
    public static void Write(Database db, string folder)
    {
        using var c = db.Open();
        foreach (var old in new[] { SagasFolder, LooseFolder }.Select(f => Path.Combine(folder, f)).Where(Directory.Exists)) Directory.Delete(old, true);

        var fields = c.Query<(string Entity, long EntityId, string Field, string? Value, string Source, bool Manual)>(
                "SELECT entity, entity_id AS EntityId, field, value, source, manual FROM field_value ORDER BY entity, entity_id, field, source")
            .ToLookup(f => (f.Entity, f.EntityId), f => new PublishedField(f.Field, f.Value, f.Source, f.Manual));
        var criteria = c.Query<(long VersionId, string Criterion, string? Value, string? Text)>(
                "SELECT version_id AS VersionId, criterion, value, text FROM version_criterion ORDER BY version_id, criterion")
            .ToLookup(k => k.VersionId, k => new PublishedCriterion(k.Criterion, k.Value, k.Text));
        var codes = c.Query<(long VersionId, string Code)>("SELECT version_id, code FROM version_code").ToLookup(x => x.VersionId, x => x.Code);
        var versions = c.Query<(long Id, long GameId, string Platform, string Kind, long? Year, long? IgdbId, long? SteamAppId)>(
                "SELECT id, game_id AS GameId, platform, kind, year, igdb_id AS IgdbId, steam_app_id AS SteamAppId FROM version ORDER BY id")
            .ToLookup(v => v.GameId, v => new PublishedVersion(v.Id, v.Platform, v.Kind, (int?)v.Year, v.IgdbId, v.SteamAppId, criteria[v.Id].ToList(),
                codes[v.Id].Any() ? codes[v.Id].Order(StringComparer.Ordinal).ToList() : null));
        var images = c.Query<(long GameId, string Kind, string Url)>("SELECT game_id AS GameId, kind, url FROM image WHERE game_id IN (SELECT id FROM game) ORDER BY game_id, kind")
            .GroupBy(i => i.GameId).ToDictionary(g => g.Key, g => g.ToDictionary(i => i.Kind, i => i.Url));
        // Solo los ports que cruzaron con un juego (#69): los que quedan sin juego no salen.
        var ports = c.Query<(long Id, long GameId, string Console, string Project, string Kind, string? Toolchain, string Status, bool Abandoned, string Link, string Source,
                string Added, string? Emulator, long? Level, bool Delisted)>("""
                SELECT id, game_id, console, project, kind, toolchain, status, abandoned, link, source, added, emulator, emulator_level, delisted FROM port
                WHERE game_id IN (SELECT id FROM game) ORDER BY game_id, id
                """)
            .ToLookup(p => p.GameId, p => new PublishedPort(p.Id, p.Console, p.Project, p.Kind, p.Toolchain, p.Status, p.Status == Ports.Complete,
                p.Abandoned, p.Link, p.Source, p.Added, p.Emulator, (int?)p.Level, p.Delisted ? true : null));
        var games = c.Query<(long Id, long? SagaId, string NameEs, string? OriginalName, long? Year, long? IgdbId, long? Popularity, bool Iconic)>(
                "SELECT id, saga_id AS SagaId, name_es AS NameEs, original_name AS OriginalName, year, igdb_id AS IgdbId, popularity, iconic FROM game ORDER BY id")
            .ToLookup(g => g.SagaId, g => new PublishedGame(g.Id, g.NameEs, g.OriginalName, (int?)g.Year, g.IgdbId, g.Popularity, g.Iconic, fields[("game", g.Id)].ToList(), versions[g.Id].ToList(), images.GetValueOrDefault(g.Id),
                ports[g.Id].Any() ? ports[g.Id].ToList() : null));
        var steps = c.Query<(long RouteId, long Position, long GameId, long? VersionId, string? Reason)>(
                "SELECT route_id AS RouteId, position, game_id AS GameId, recommended_version_id AS VersionId, reason FROM route_step ORDER BY route_id, position")
            .ToLookup(s => s.RouteId, s => new PublishedStep((int)s.Position, s.GameId, s.VersionId, s.Reason));
        var routes = c.Query<(long Id, long SagaId, string Kind, string? Rationale, string? Sources, bool Approved)>(
                "SELECT id, saga_id AS SagaId, kind, rationale, sources, approved FROM route ORDER BY id")
            .ToLookup(r => r.SagaId, r => new PublishedRoute(r.Id, r.Kind, r.Rationale, r.Sources, r.Approved, steps[r.Id].ToList()));

        foreach (var s in c.Query<(long Id, string Name, string? IgdbKind, long? IgdbId, bool Iconic)>("SELECT id, name, igdb_kind AS IgdbKind, igdb_id AS IgdbId, iconic FROM saga ORDER BY id"))
            Save(Path.Combine(folder, SagasFolder, $"{s.Id:D5}-{Slug(s.Name)}.json"),
                new PublishedSaga(s.Id, s.Name, s.IgdbKind, s.IgdbId, s.Iconic, fields[("saga", s.Id)].ToList(), games[s.Id].ToList(), routes[s.Id].ToList()));
        foreach (var part in games[null].GroupBy(g => g.Id / LoosePerFile))
            Save(Path.Combine(folder, LooseFolder, $"{part.Key:D5}.json"), new PublishedLooseGames(part.ToList()));

        var platforms = c.Query<(long IgdbId, string ConsoleId)>("SELECT igdb_id AS IgdbId, console_id AS ConsoleId FROM console_platform ORDER BY igdb_id").ToLookup(p => p.ConsoleId, p => p.IgdbId);
        Save(Path.Combine(folder, ConsolesFile), c.Query<(string Id, string Name, string? Company, string? Status)>("SELECT id, name, company, emulation_status AS Status FROM console ORDER BY rowid")
            .Select(k => new PublishedConsole(k.Id, k.Name, k.Company, k.Status, platforms[k.Id].ToList())).ToList());
        Save(Path.Combine(folder, ReplacedFile), c.Query<(string Kind, long OldId, long? NewId)>("SELECT kind, old_id AS OldId, new_id AS NewId FROM replaced ORDER BY kind, old_id")
            .Select(r => new PublishedReplacement(r.Kind, r.OldId, r.NewId)).ToList());
        // #95: las recetas de «Emuladores», tal como se guardaron (las traídas de main conservan sus bytes).
        foreach (var (id, json) in c.Query<(string Id, string Json)>("SELECT id, json FROM recipe ORDER BY id"))
        {
            Directory.CreateDirectory(Path.Combine(folder, RecipesFolder));
            File.WriteAllText(Path.Combine(folder, RecipesFolder, id + ".json"), json, new UTF8Encoding(false));
        }
    }

    static string Slug(string name)
    {
        var plain = new string(name.Normalize(NormalizationForm.FormD).Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == ' ' || ch == '-').ToArray()).Trim().ToLowerInvariant();
        var slug = string.Join('-', plain.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "saga" : slug[..Math.Min(slug.Length, 60)].TrimEnd('-');
    }

    /// <summary>Cuántos juegos tienen cada tipo de imagen: el informe de `catalogo validar`.</summary>
    public static Dictionary<string, int> ImageCounts(string folder)
    {
        var all = Read(folder);
        var images = all.Sagas.SelectMany(s => s.Games).Concat(all.Loose).SelectMany(g => g.Images?.Keys ?? Enumerable.Empty<string>()).ToList();
        return SteamGridDb.Kinds.ToDictionary(k => k, k => images.Count(i => i == k));
    }

    sealed record Everything(List<PublishedSaga> Sagas, List<PublishedGame> Loose, List<PublishedConsole> Consoles, List<PublishedReplacement> Replaced);

    static Everything Read(string folder)
    {
        var sagaFiles = Directory.Exists(Path.Combine(folder, SagasFolder)) ? Directory.GetFiles(Path.Combine(folder, SagasFolder), "*.json") : [];
        var looseFiles = Directory.Exists(Path.Combine(folder, LooseFolder)) ? Directory.GetFiles(Path.Combine(folder, LooseFolder), "*.json") : [];
        return new(sagaFiles.Select(Load<PublishedSaga>).ToList(), looseFiles.SelectMany(f => Load<PublishedLooseGames>(f).Games).ToList(),
            Load<List<PublishedConsole>>(Path.Combine(folder, ConsolesFile)), Load<List<PublishedReplacement>>(Path.Combine(folder, ReplacedFile)));
    }

    static readonly string[] VersionKinds = ["original", "remaster", "remake"];
    static readonly string[] PortKinds = ["decomp", "recomp", "remake", "port", "native-layer", "engine"];

    /// <summary>Lo que está mal en lo publicado; vacía si se puede compilar. Corre en cada PR.</summary>
    public static List<string> Validate(string folder)
    {
        Everything all;
        try { all = Read(folder); }
        catch (Exception e) when (e is JsonException or IOException or NullReferenceException or ArgumentNullException) { return [$"No se puede leer: {e.Message}"]; }

        var errors = new List<string>();
        void Repeated(string what, IEnumerable<long> ids)
        {
            foreach (var id in ids.GroupBy(i => i).Where(g => g.Count() > 1)) errors.Add($"{what} {id.Key} está repetido.");
        }
        var games = all.Sagas.SelectMany(s => s.Games).Concat(all.Loose).ToList();
        var versions = games.SelectMany(g => g.Versions.Select(v => (Game: g, Version: v))).ToList();
        Repeated("La saga", all.Sagas.Select(s => s.Id));
        Repeated("El juego", games.Select(g => g.Id));
        Repeated("La versión", versions.Select(v => v.Version.Id));
        var (gameIds, versionIds, consoles) = (games.Select(g => g.Id).ToHashSet(), versions.Select(v => v.Version.Id).ToHashSet(), all.Consoles.Select(k => k.Id).ToHashSet());
        var sagaIds = all.Sagas.Select(s => s.Id).ToHashSet();

        foreach (var s in all.Sagas.Where(s => string.IsNullOrWhiteSpace(s.Name))) errors.Add($"La saga {s.Id} no tiene nombre.");
        foreach (var g in games.Where(g => string.IsNullOrWhiteSpace(g.NameEs))) errors.Add($"El juego {g.Id} no tiene nombre.");
        foreach (var (g, v) in versions)
        {
            if (!consoles.Contains(v.Platform)) errors.Add($"La versión {v.Id} de «{g.NameEs}» usa una consola que no existe: {v.Platform}.");
            if (!VersionKinds.Contains(v.Kind)) errors.Add($"La versión {v.Id} de «{g.NameEs}» tiene un tipo desconocido: {v.Kind}.");
        }
        foreach (var g in games)
            foreach (var (kind, url) in g.Images ?? [])
            {
                if (!SteamGridDb.Kinds.Contains(kind)) errors.Add($"El juego {g.Id} tiene una imagen de tipo desconocido: {kind}.");
                if (!ImageRefs.IsAllowed(url)) errors.Add($"La imagen {kind} del juego {g.Id} no es de SteamGridDB ni de IGDB: {url}.");
            }
        Repeated("El port", games.SelectMany(g => g.Ports ?? []).Select(p => p.Id));
        foreach (var g in games)
            foreach (var p in g.Ports ?? [])
            {
                if (!consoles.Contains(p.Platform)) errors.Add($"El port {p.Id} de «{g.NameEs}» usa una consola que no existe: {p.Platform}.");
                if (!PortKinds.Contains(p.Kind)) errors.Add($"El port {p.Id} de «{g.NameEs}» tiene un tipo desconocido: {p.Kind}.");
                if (!Ports.Statuses.Contains(p.Status)) errors.Add($"El port {p.Id} de «{g.NameEs}» tiene un estado desconocido: {p.Status}.");
                if (!Uri.TryCreate(p.Link, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
                    errors.Add($"El enlace del port {p.Id} de «{g.NameEs}» no es una dirección web: {p.Link}.");
                if (p.FullyPlayable != (p.Status == Ports.Complete)) errors.Add($"El port {p.Id} de «{g.NameEs}» dice 100 % jugable sin estar completo, o al revés.");
                if (p.Status == Ports.NoPort && p.Kind != "decomp") errors.Add($"El port {p.Id} de «{g.NameEs}» está sin port todavía, pero no es una decompilación.");
                if (p.EmulatorLevel is < 0 or > 3) errors.Add($"El port {p.Id} de «{g.NameEs}» tiene un nivel de emulador fuera de 0 a 3.");
            }
        foreach (var r in all.Sagas.SelectMany(s => s.Routes))
            foreach (var step in r.Steps)
            {
                if (!gameIds.Contains(step.GameId)) errors.Add($"La ruta {r.Id} apunta a un juego que no existe: {step.GameId}.");
                if (step.RecommendedVersionId is { } v && !versionIds.Contains(v)) errors.Add($"La ruta {r.Id} recomienda una versión que no existe: {v}.");
            }
        foreach (var r in all.Replaced)
        {
            var known = r.Kind switch { "game" => gameIds, "version" => versionIds, "saga" => sagaIds, _ => null };
            if (known is null) errors.Add($"Reemplazo de tipo desconocido: {r.Kind}.");
            else if (known.Contains(r.OldId)) errors.Add($"{r.Kind} {r.OldId} figura como reemplazado, pero sigue en el catálogo.");
            else if (r.NewId is { } to && !known.Contains(to)) errors.Add($"{r.Kind} {r.OldId} se reemplaza por algo que no existe: {to}.");
        }
        ValidateRecipes(folder, errors);
        return errors;
    }

    static readonly string[] ContentTables = ["console", "console_platform", "saga", "game", "version", "version_criterion", "field_value", "route", "route_step", "image", "replaced", "version_code", "port", "recipe", "recipe_console"];

    /// <summary>
    /// SHA-256 de todas las filas publicadas, tabla por tabla y ordenadas por todas sus columnas (#44). La base comprimida no sale igual byte a
    /// byte de una compilación a otra; esto sí, si los datos son los mismos.
    /// </summary>
    static string ContentHash(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in ContentTables)
        {
            var columns = c.Query<string>($"SELECT name FROM pragma_table_info('{table}', 'catalog') ORDER BY cid").ToList();
            if (columns.Count == 0) continue;
            hash.AppendData(Encoding.UTF8.GetBytes($"#{table}\n"));
            using var reader = c.ExecuteReader($"SELECT {string.Join(", ", columns)} FROM catalog.{table} ORDER BY {string.Join(", ", columns)}");
            while (reader.Read())
            {
                var row = new StringBuilder();
                // Separadores que no aparecen en los datos: NUL para un valor vacío, US entre columnas y salto de línea entre filas.
                for (var i = 0; i < reader.FieldCount; i++) row.Append(reader.IsDBNull(i) ? "\u0000" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('\u001f');
                hash.AppendData(Encoding.UTF8.GetBytes(row.Append('\n').ToString()));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Genera la base que baja la app y su manifiesto. Parte siempre de los archivos y de una base nueva, nunca de la del generador: lo que no
    /// esté en lo publicado no puede colarse en lo que reciben las personas.
    /// </summary>
    /// <param name="validate">False solo para rearmar una base vieja como origen de una fusión (#104): lo de un commit ya publicado no se vuelve a juzgar.</param>
    public static PublishedManifest Compile(string folder, string output, bool validate = true)
    {
        if (validate && Validate(folder) is { Count: > 0 } errors) throw new InvalidDataException(string.Join('\n', errors));
        var all = Read(folder);
        var work = Directory.CreateTempSubdirectory("otteretro-compilar").FullName;
        try
        {
            var db = new Database(Path.Combine(work, "propia.db"), Path.Combine(work, "catalogo.db"));
            Migrations.Apply(db);
            var games = all.Sagas.SelectMany(s => s.Games.Select(g => (SagaId: (long?)s.Id, Game: g))).Concat(all.Loose.Select(g => (SagaId: (long?)null, Game: g))).ToList();
            using (var c = db.Open())
            using (var t = c.BeginTransaction())
            {
                foreach (var k in all.Consoles)
                {
                    c.Execute("INSERT OR REPLACE INTO catalog.console (id, name, company, emulation_status) VALUES (@Id, @Name, @Company, @EmulationStatus)", k, t);
                    foreach (var p in k.IgdbPlatforms) c.Execute("INSERT OR REPLACE INTO catalog.console_platform (igdb_id, console_id) VALUES (@p, @Id)", new { p, k.Id }, t);
                }
                void Fields(string entity, long id, List<PublishedField> list)
                {
                    foreach (var f in list)
                        c.Execute("INSERT INTO catalog.field_value (entity, entity_id, field, value, source, manual) VALUES (@entity, @id, @Field, @Value, @Source, @Manual)",
                            new { entity, id, f.Field, f.Value, f.Source, f.Manual }, t);
                }
                foreach (var s in all.Sagas)
                {
                    c.Execute("INSERT INTO catalog.saga (id, name, igdb_kind, igdb_id, iconic) VALUES (@Id, @Name, @IgdbKind, @IgdbId, @Iconic)", s, t);
                    Fields("saga", s.Id, s.Fields);
                }
                foreach (var (sagaId, g) in games)
                {
                    c.Execute("INSERT INTO catalog.game (id, saga_id, name_es, original_name, year, igdb_id, popularity, iconic) VALUES (@Id, @sagaId, @NameEs, @OriginalName, @Year, @IgdbId, @Popularity, @Iconic)",
                        new { g.Id, sagaId, g.NameEs, g.OriginalName, g.Year, g.IgdbId, g.Popularity, g.Iconic }, t);
                    Fields("game", g.Id, g.Fields);
                    foreach (var (kind, url) in g.Images ?? [])
                        c.Execute("INSERT INTO catalog.image (game_id, kind, url) VALUES (@Id, @kind, @url)", new { g.Id, kind, url }, t);
                    foreach (var p in g.Ports ?? [])
                        c.Execute("""
                            INSERT INTO catalog.port (id, console, game, project, kind, toolchain, status, abandoned, delisted, link, source, added, emulator, emulator_level, game_id)
                            VALUES (@Id, @Platform, @NameEs, @Project, @Kind, @Toolchain, @Status, @Abandoned, @Delisted, @Link, @Source, @Added, @Emulator, @EmulatorLevel, @GameId)
                            """, new { p.Id, p.Platform, g.NameEs, p.Project, p.Kind, p.Toolchain, p.Status, p.Abandoned, Delisted = p.Delisted == true, p.Link, p.Source, p.Added,
                                p.Emulator, p.EmulatorLevel, GameId = g.Id }, t);
                    foreach (var v in g.Versions)
                    {
                        c.Execute("INSERT INTO catalog.version (id, game_id, platform, kind, year, igdb_id, steam_app_id) VALUES (@Id, @GameId, @Platform, @Kind, @Year, @IgdbId, @SteamAppId)",
                            new { v.Id, GameId = g.Id, v.Platform, v.Kind, v.Year, v.IgdbId, v.SteamAppId }, t);
                        foreach (var code in v.Codes ?? [])
                            c.Execute("INSERT OR IGNORE INTO catalog.version_code (version_id, code) VALUES (@Id, @code)", new { v.Id, code }, t);
                        foreach (var k in v.Criteria)
                            c.Execute("INSERT INTO catalog.version_criterion (version_id, criterion, value, text) VALUES (@VersionId, @Criterion, @Value, @Text)", new { VersionId = v.Id, k.Criterion, k.Value, k.Text }, t);
                    }
                }
                foreach (var s in all.Sagas)
                    foreach (var r in s.Routes)
                    {
                        c.Execute("INSERT INTO catalog.route (id, saga_id, kind, rationale, sources, approved) VALUES (@Id, @SagaId, @Kind, @Rationale, @Sources, @Approved)",
                            new { r.Id, SagaId = s.Id, r.Kind, r.Rationale, r.Sources, r.Approved }, t);
                        foreach (var step in r.Steps)
                            c.Execute("INSERT INTO catalog.route_step (route_id, position, game_id, recommended_version_id, reason) VALUES (@RouteId, @Position, @GameId, @RecommendedVersionId, @Reason)",
                                new { RouteId = r.Id, step.Position, step.GameId, step.RecommendedVersionId, step.Reason }, t);
                    }
                foreach (var r in all.Replaced) c.Execute("INSERT INTO catalog.replaced (kind, old_id, new_id) VALUES (@Kind, @OldId, @NewId)", r, t);
                foreach (var r in ReadRecipes(folder))
                {
                    c.Execute("INSERT INTO catalog.recipe (id, kind, json) VALUES (@Id, @Kind, @Json)", r, t);
                    foreach (var console in r.Consoles.Distinct())
                        c.Execute("INSERT INTO catalog.recipe_console (recipe_id, console_id) VALUES (@Id, @console)", new { r.Id, console }, t);
                }
                t.Commit();
            }
            var plain = Path.Combine(work, "publicado.db");
            string content;
            string[] schema;
            using (var c = db.Open())
            {
                content = ContentHash(c);
                // #79: "tabla.columna" de cada tabla de la base publicada, ordenadas (ordinal); no cuenta sqlite_* ni catalog_migration.
                schema = c.Query<string>("SELECT m.name || '.' || p.name FROM catalog.sqlite_master m, pragma_table_info(m.name, 'catalog') p WHERE m.type = 'table' AND substr(m.name, 1, 7) <> 'sqlite_' AND m.name <> 'catalog_migration'")
                    .Order(StringComparer.Ordinal).ToArray();
                c.Execute("VACUUM catalog INTO @plain", new { plain });
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using (var input = File.OpenRead(plain))
            using (var gz = new GZipStream(File.Create(output), CompressionLevel.SmallestSize))
                input.CopyTo(gz);
            using var stream = File.OpenRead(output);
            return new(Convert.ToHexStringLower(SHA256.HashData(stream)), all.Sagas.Count, games.Count, games.Sum(g => g.Game.Versions.Count), content, schema);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>Una versión publicada del catálogo: una release `catalogo-*` del repo al que se publica (#104, decisión 362).</summary>
/// <param name="Commit">El commit de main del tag (los 7 caracteres del final).</param>
/// <param name="Current">Es la base del catálogo local.</param>
/// <param name="Changes">Lo que trajo según `cambios.json` (#105); vacío si la versión no tiene historial.</param>
/// <param name="Images">Cuántos juegos cambiaron de imagen en ella.</param>
public record CatalogRelease(string Tag, string Commit, DateTimeOffset? Published, bool Current, List<PublishedChange> Changes, int Images);
/// <summary>Ajustes › Catálogo (#105, decisión 363): la base, lo que hay en la base local y las versiones publicadas.</summary>
/// <param name="Base">El commit de main del que parte el catálogo local; null si no se fijó.</param>
public record CatalogVersionsPage(string? Base, long Sagas, long Games, long Images, List<CatalogRelease> Versions);
/// <param name="Inserted">Filas que entraron, cambiaron o se quitaron con la versión, sumando todas las tablas.</param>
/// <param name="Conflicts">Campos o filas en conflicto que quedan para elegir (<see cref="CatalogVersions.Conflicts"/>).</param>
public record CatalogApplyResult(string Tag, string Commit, int Inserted, int Updated, int Deleted, int Conflicts);
/// <summary>Un campo (o una fila entera, si <paramref name="Field"/> es null) cambiado distinto en la versión y acá (#104, decisión 359).</summary>
/// <param name="Key">La clave de la fila como objeto JSON, p. ej. {"id":10}.</param>
/// <param name="Base">Lo de la base, lo local y lo de la versión, como JSON; null si la fila no existe en ese lado.</param>
public record CatalogConflict(long Id, string Version, string Table, string Key, string? Field, string? Base, string? Local, string? Remote);

/// <summary>
/// Bajar una versión publicada del catálogo y fusionarla con la base local (#104), en tres vías por fila y campo sobre las tablas publicadas:
/// el origen es lo publicado en la base (`catalog.base`). Lo que cambió solo en la versión entra, lo cambiado solo acá se queda y un mismo
/// campo distinto en los dos lados queda en `catalog_conflict` para elegir; nada se pisa sin elegir. Al terminar, la base es el commit de
/// la versión y su base compilada se guarda como origen de la próxima fusión.
/// </summary>
public sealed class CatalogVersions(Database db, GitHubPublisher github)
{
    const string Prefix = "catalogo-", DbAsset = "catalogo.db.gz", ManifestAsset = "catalogo.db.gz.json", HistoryAsset = "cambios.json";

    /// <summary>Lo publicado (<see cref="InitialCatalog"/>.Shared sin `claude_fingerprint`, que no se publica), padres antes que hijos.</summary>
    static readonly string[] Tables = ["console", "console_platform", "saga", "game", "version", "version_criterion", "field_value", "route", "route_step", "image", "replaced", "version_code", "port"];
    /// <summary>Columnas que lo publicado rellena con otra cosa: `port.game` es el nombre de la fuente acá y el del juego en lo compilado.</summary>
    static readonly HashSet<string> Ignored = ["port.game"];

    /// <summary>Las versiones `catalogo-*` publicadas, la más nueva primero, con lo que trajo cada una.</summary>
    public async Task<List<CatalogRelease>> List(CancellationToken ct = default)
    {
        var baseSha = Base();
        var releases = (await Releases(ct))
            .Select(r => (Node: r, Tag: r["tag_name"]!.GetValue<string>(), Date: r["published_at"]?.GetValue<string>() is { } d ? DateTimeOffset.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null))
            .OrderByDescending(r => r.Date).ToList();
        // `cambios.json` acumula todo el historial (#31): basta el de la más nueva que lo tenga.
        var history = releases.Select(r => AssetUrl(r.Node, HistoryAsset)).FirstOrDefault(u => u is not null) is { } url
            ? JsonSerializer.Deserialize<PublishedHistory>(await github.Bytes(url, ct), JsonSerializerOptions.Web)
            : null;
        return releases.Select(r => new CatalogRelease(r.Tag, r.Tag.Split('-')[^1], r.Date, baseSha is not null && baseSha.StartsWith(r.Tag.Split('-')[^1], StringComparison.Ordinal),
            history?.Entries.Where(e => e.Version == r.Tag).ToList() ?? [], history?.Images?.GetValueOrDefault(r.Tag) ?? 0)).ToList();
    }

    /// <summary>La pestaña entera: la base, los totales de la base local y las versiones.</summary>
    public async Task<CatalogVersionsPage> Page(CancellationToken ct = default)
    {
        var versions = await List(ct);
        using var c = db.Open();
        return new(Base(), c.ExecuteScalar<long>("SELECT count(*) FROM saga"), c.ExecuteScalar<long>("SELECT count(*) FROM game"),
            c.ExecuteScalar<long>("SELECT count(*) FROM image"), versions);
    }

    static string? AssetUrl(JsonNode release, string name) =>
        release["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == name)?["browser_download_url"]?.GetValue<string>();

    async Task<List<JsonNode>> Releases(CancellationToken ct) =>
        ((await github.Get($"repos/{GitHubPublisher.Repo}/releases?per_page=100", ct))?.AsArray() ?? [])
        .Where(r => r?["tag_name"]?.GetValue<string>().StartsWith(Prefix, StringComparison.Ordinal) == true).Select(r => r!).ToList();

    /// <summary>Baja la versión, la fusiona con la base local y deja su commit como base.</summary>
    public async Task<CatalogApplyResult> Apply(string tag, CancellationToken ct = default)
    {
        await GitHubPublisher.Gate.WaitAsync(ct);
        try { return await ApplyLocked(tag, ct); }
        finally { GitHubPublisher.Gate.Release(); }
    }

    async Task<CatalogApplyResult> ApplyLocked(string tag, CancellationToken ct)
    {
        // Con conflictos sin elegir, otra fusión los taparía: lo local quedaría como cambio propio y lo de la versión se perdería.
        if (Conflicts(db).Count is > 0 and var pending) throw new TaskException(ApiError.Of("publish.pendingConflicts", ("count", pending.ToString())));
        var baseSha = Base() ?? throw new TaskException(ApiError.Of("publish.noBase"));
        var release = (await Releases(ct)).FirstOrDefault(r => r["tag_name"]!.GetValue<string>() == tag) ?? throw new TaskException(ApiError.Of("publish.noRelease", ("tag", tag)));
        string Asset(string name) => AssetUrl(release, name) ?? throw new TaskException(ApiError.Of("publish.noRelease", ("tag", tag)));
        var commit = (await github.Must(HttpMethod.Get, $"repos/{GitHubPublisher.Repo}/commits/{tag}", null, ct))["sha"]!.GetValue<string>();

        var gz = await github.Bytes(Asset(DbAsset), ct);
        var manifest = JsonNode.Parse(await github.Bytes(Asset(ManifestAsset), ct))!;
        if (!string.Equals(manifest["sha256"]?.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(gz)), StringComparison.OrdinalIgnoreCase))
            throw new TaskException(ApiError.Of("publish.badDownload", ("tag", tag)));
        Directory.CreateDirectory(github.Work);
        var remote = Path.Combine(github.Work, $"version-{tag}.db");
        Unzip(gz, remote);
        var origin = await Origin(baseSha, ct);

        var result = Merge(db, origin, remote, tag);
        // La copia de lo publicado en la base nueva, origen de la próxima fusión; se nombra por commit, así una base vieja nunca se toma por otra.
        SqliteConnection.ClearAllPools();
        var folder = Path.GetDirectoryName(OriginPath(commit))!;
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "base-*.db")) File.Delete(old);
        File.Copy(remote, OriginPath(commit));
        GitHubPublisher.SetBase(db, commit);
        return result with { Commit = commit };
    }

    string? Base()
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @BaseSetting", new { GitHubPublisher.BaseSetting });
    }

    string OriginPath(string sha) => Path.Combine(Path.GetDirectoryName(db.CatalogPath)!, "versiones", $"base-{sha}.db");

    /// <summary>Lo publicado en la base: la copia guardada al aplicar, o la primera vez, los JSON de ese commit compilados.</summary>
    async Task<string> Origin(string sha, CancellationToken ct)
    {
        if (File.Exists(OriginPath(sha))) return OriginPath(sha);
        var file = Path.Combine(github.Work, $"origen-{sha}.db");
        var compiled = file + ".gz";
        Published.Compile(await github.MainFolder(sha, ct), compiled, validate: false);
        Unzip(await File.ReadAllBytesAsync(compiled, ct), file);
        return file;
    }

    static void Unzip(byte[] gz, string target)
    {
        SqliteConnection.ClearAllPools();
        using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var output = File.Create(target);
        input.CopyTo(output);
    }

    /// <summary>La fusión en tres vías, toda en una transacción: si algo falla, el catálogo local queda como estaba.</summary>
    static CatalogApplyResult Merge(Database db, string origin, string remote, string tag)
    {
        var (inserted, updated, deleted) = (0, 0, 0);
        var conflicts = new List<CatalogConflict>();
        using var c = db.Open();
        c.Execute("ATTACH DATABASE @origin AS o; ATTACH DATABASE @remote AS r;", new { origin, remote });
        // Sin claves foráneas: un borrado en cascada se llevaría hijos cambiados acá. Cada fila se decide por sí sola, también los hijos.
        c.Execute("PRAGMA foreign_keys = OFF");
        try
        {
            using var t = c.BeginTransaction();
            foreach (var table in Tables)
            {
                var info = c.Query<(string Name, long Pk)>($"SELECT name, pk FROM pragma_table_info('{table}', 'catalog')", transaction: t).ToList();
                var shared = new[] { "o", "r" }.Select(s => c.Query<string>($"SELECT name FROM pragma_table_info('{table}', '{s}')", transaction: t).ToHashSet()).ToList();
                var key = info.Where(i => i.Pk > 0).OrderBy(i => i.Pk).Select(i => i.Name).ToList();
                if (key.Count == 0 || !key.All(k => shared.All(s => s.Contains(k)))) continue;
                var columns = key.Concat(info.Where(i => i.Pk == 0 && shared.All(s => s.Contains(i.Name)) && !Ignored.Contains($"{table}.{i.Name}")).Select(i => i.Name)).ToList();
                var (o, r, l) = (Rows(c, t, "o", table, columns, key.Count), Rows(c, t, "r", table, columns, key.Count), Rows(c, t, "catalog", table, columns, key.Count));

                foreach (var id in o.Keys.Union(r.Keys).Union(l.Keys).ToList())
                {
                    var (before, theirs, ours) = (o.GetValueOrDefault(id), r.GetValueOrDefault(id), l.GetValueOrDefault(id));
                    if (Same(theirs, ours) || Same(before, theirs)) continue;
                    var where = string.Join(" AND ", key.Select((k, i) => $"{k} = @k{i}"));
                    DynamicParameters Key()
                    {
                        var p = new DynamicParameters();
                        for (var i = 0; i < key.Count; i++) p.Add($"k{i}", Value((theirs ?? ours ?? before)![i]));
                        return p;
                    }
                    if (Same(before, ours))
                    {
                        // Cambió solo en la versión: entra tal cual.
                        if (theirs is null) { c.Execute($"DELETE FROM catalog.{table} WHERE {where}", Key(), t); deleted++; }
                        else if (ours is null) { Insert(c, t, table, columns, theirs); inserted++; }
                        else { Update(c, t, table, columns, key, theirs, Enumerable.Range(key.Count, columns.Count - key.Count).Where(i => theirs[i] != ours[i])); updated++; }
                        continue;
                    }
                    var rowKey = new JsonObject(key.Select((k, i) => KeyValuePair.Create(k, JsonNode.Parse((theirs ?? ours ?? before)![i])))).ToJsonString();
                    if (theirs is null || ours is null)
                    {
                        // Quitada en un lado y cambiada en el otro: se elige la fila entera.
                        conflicts.Add(new(0, tag, table, rowKey, null, Row(columns, before), Row(columns, ours), Row(columns, theirs)));
                        continue;
                    }
                    // Las dos con la fila: campo por campo. Lo que cambió solo en la versión entra; lo distinto en los dos lados, a elegir.
                    var take = new List<int>();
                    for (var i = key.Count; i < columns.Count; i++)
                    {
                        if (theirs[i] == ours[i] || (before is not null && before[i] == theirs[i])) continue;
                        if (before is not null && before[i] == ours[i]) take.Add(i);
                        else conflicts.Add(new(0, tag, table, rowKey, columns[i], before?[i], ours[i], theirs[i]));
                    }
                    if (take.Count > 0) { Update(c, t, table, columns, key, theirs, take); updated++; }
                }
            }
            foreach (var x in conflicts)
                c.Execute("INSERT INTO catalog_conflict (version, table_name, row_key, field, base, local, remote) VALUES (@Version, @Table, @Key, @Field, @Base, @Local, @Remote)", x, t);
            t.Commit();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: p. ej. un igdb_id único que pasó de un juego a otro
        {
            throw new TaskException(ApiError.Of("publish.mergeFailed", ("tag", tag)));
        }
        finally
        {
            c.Execute("PRAGMA foreign_keys = ON; DETACH DATABASE o; DETACH DATABASE r;");
        }
        return new(tag, "", inserted, updated, deleted, conflicts.Count);
    }

    /// <summary>Las filas de una tabla por su clave, cada valor como JSON (así null, número y texto se comparan sin perder el tipo).</summary>
    // ponytail: las tres copias en memoria (unas 100.000 filas de field_value); por tabla y en orden de clave si llega a pesar.
    static Dictionary<string, string[]> Rows(SqliteConnection c, SqliteTransaction t, string schema, string table, List<string> columns, int keys)
    {
        var rows = new Dictionary<string, string[]>();
        using var reader = c.ExecuteReader($"SELECT {string.Join(", ", columns)} FROM {schema}.{table}", transaction: t);
        while (reader.Read())
        {
            var row = new string[columns.Count];
            for (var i = 0; i < row.Length; i++) row[i] = JsonSerializer.Serialize(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows[string.Join('\u001f', row[..keys])] = row;
        }
        return rows;
    }

    static bool Same(string[]? a, string[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);

    static string? Row(List<string> columns, string[]? row) =>
        row is null ? null : new JsonObject(columns.Select((k, i) => KeyValuePair.Create(k, JsonNode.Parse(row[i])))).ToJsonString();

    /// <summary>El valor de vuelta desde su JSON.</summary>
    static object? Value(string json) => JsonDocument.Parse(json).RootElement switch
    {
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.True } => 1L,
        { ValueKind: JsonValueKind.False } => 0L,
        var n when n.TryGetInt64(out var i) => i,
        var n => n.GetDouble(),
    };

    static void Insert(SqliteConnection c, SqliteTransaction t, string table, List<string> columns, string[] row)
    {
        var p = new DynamicParameters();
        for (var i = 0; i < columns.Count; i++) p.Add($"c{i}", Value(row[i]));
        c.Execute($"INSERT INTO catalog.{table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@c{i}"))})", p, t);
    }

    static void Update(SqliteConnection c, SqliteTransaction t, string table, List<string> columns, List<string> key, string[] row, IEnumerable<int> changed)
    {
        var set = changed.ToList();
        if (set.Count == 0) return;
        var p = new DynamicParameters();
        for (var i = 0; i < columns.Count; i++) p.Add($"c{i}", Value(row[i]));
        c.Execute($"UPDATE catalog.{table} SET {string.Join(", ", set.Select(i => $"{columns[i]} = @c{i}"))} WHERE {string.Join(" AND ", key.Select((k, i) => $"{k} = @c{i}"))}", p, t);
    }

    /// <summary>Los conflictos que quedan por elegir.</summary>
    public static List<CatalogConflict> Conflicts(Database db)
    {
        using var c = db.Open();
        return c.Query<CatalogConflict>("SELECT id, version, table_name AS \"Table\", row_key AS \"Key\", field, base, local, remote FROM catalog_conflict ORDER BY id").ToList();
    }

    /// <summary>Elige un lado de un conflicto: lo local lo deja como está; lo de la versión lo escribe. El conflicto deja de figurar.</summary>
    /// <returns>False si ese conflicto no existe.</returns>
    public static bool Resolve(Database db, long id, bool useRemote)
    {
        using var c = db.Open();
        var x = c.QuerySingleOrDefault<CatalogConflict>("SELECT id, version, table_name AS \"Table\", row_key AS \"Key\", field, base, local, remote FROM catalog_conflict WHERE id = @id", new { id });
        if (x is null) return false;
        if (!Tables.Contains(x.Table)) throw new InvalidDataException(x.Table);
        c.Execute("PRAGMA foreign_keys = OFF");
        try
        {
            using var t = c.BeginTransaction();
            if (useRemote)
            {
                var key = JsonNode.Parse(x.Key)!.AsObject().Select(k => (k.Key, Json: k.Value?.ToJsonString() ?? "null")).ToList();
                var p = new DynamicParameters();
                for (var i = 0; i < key.Count; i++) p.Add($"k{i}", Value(key[i].Json));
                var where = string.Join(" AND ", key.Select((k, i) => $"{k.Key} = @k{i}"));
                if (x.Field is not null)
                {
                    p.Add("v", Value(x.Remote ?? "null"));
                    c.Execute($"UPDATE catalog.{x.Table} SET {x.Field} = @v WHERE {where}", p, t);
                }
                else if (x.Remote is null) c.Execute($"DELETE FROM catalog.{x.Table} WHERE {where}", p, t);
                else
                {
                    var row = JsonNode.Parse(x.Remote)!.AsObject().Select(k => (k.Key, Json: k.Value?.ToJsonString() ?? "null")).ToList();
                    var columns = row.Select(k => k.Key).ToList();
                    var values = row.Select(k => k.Json).ToArray();
                    // Quitada acá y cambiada en la versión: vuelve con lo de la versión.
                    if (c.ExecuteScalar<long>($"SELECT count(*) FROM catalog.{x.Table} WHERE {where}", p, t) == 0) Insert(c, t, x.Table, columns, values);
                    else Update(c, t, x.Table, columns, key.Select(k => k.Key).ToList(), values, Enumerable.Range(0, columns.Count).Where(i => !key.Any(k => k.Key == columns[i])));
                }
            }
            c.Execute("DELETE FROM catalog_conflict WHERE id = @id", new { id }, t);
            t.Commit();
        }
        finally { c.Execute("PRAGMA foreign_keys = ON"); }
        return true;
    }
}

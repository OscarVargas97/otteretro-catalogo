using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Otteretro.Data;

/// <summary>Aplica en orden los Data/Migrations/NNNN_nombre.sql que aún no se aplicaron (A1).</summary>
public static class Migrations
{
    const string Marker = ".Migrations.";

    /// <summary>Devuelve cuántas migraciones aplicó.</summary>
    /// <param name="until">Para las pruebas: la última que se aplica (una base como la de una versión anterior).</param>
    public static int Apply(Database db, string? until = null)
    {
        using var c = db.Open();
        RenameLegacyLog(c);
        c.Execute("CREATE TABLE IF NOT EXISTS migration (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL)");
        var done = c.Query<string>("SELECT name FROM migration").ToHashSet();
        if (HasCatalogLog(c)) done.UnionWith(c.Query<string>("SELECT name FROM catalog.catalog_migration"));
        var assembly = typeof(Migrations).Assembly;
        var applied = 0;

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(r => r.Contains(Marker) && r.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var name = resource[(resource.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
            if (done.Contains(name)) continue;
            if (until is not null && string.CompareOrdinal(name, until) > 0) break;

            if (name == SplitMigration)
            {
                SplitCatalog(db, c, name);
                applied++;
                continue;
            }
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var sql = reader.ReadToEnd();
            // Una transacción escribe un solo archivo: en WAL, SQLite no la hace atómica entre los dos. Lo del catálogo (después de
            // la separación) se anota en el propio catálogo; esas migraciones no tocan la base propia.
            var onCatalog = string.CompareOrdinal(name, SplitMigration) > 0 && sql.Contains("catalog.", StringComparison.Ordinal);
            using var t = c.BeginTransaction();
            c.Execute(sql, transaction: t);
            if (onCatalog) c.Execute("INSERT INTO catalog.catalog_migration (name, applied_at) VALUES (@name, @date)", new { name, date = DateTime.UtcNow.ToString("O") }, t);
            else Done(c, name, t);
            t.Commit();
            applied++;
        }
        DropPersonalColumns(c);
        return applied;
    }

    static bool HasCatalogLog(SqliteConnection c) =>
        c.ExecuteScalar<long>("SELECT count(*) FROM catalog.sqlite_master WHERE type = 'table' AND name = 'catalog_migration'") > 0;

    /// <summary>
    /// Lo propio que quedó en tablas del catálogo se quita (#167). Va aparte de la separación y se mira en cada arranque: si un
    /// corte la interrumpe, se completa en el siguiente.
    /// </summary>
    static void DropPersonalColumns(SqliteConnection c)
    {
        foreach (var (table, column) in PersonalColumns)
            if (c.ExecuteScalar<long>($"SELECT count(*) FROM pragma_table_info('{table}', 'catalog') WHERE name = @column", new { column }) > 0)
                c.Execute($"ALTER TABLE catalog.{table} DROP COLUMN {column}");
    }

    static void Done(SqliteConnection c, string name, SqliteTransaction t) =>
        c.Execute("INSERT INTO migration (name, applied_at) VALUES (@name, @date)", new { name, date = DateTime.UtcNow.ToString("O") }, t);

    const string SplitMigration = "0013_catalog.sql";

    /// <summary>Las tablas del catálogo, que pasan a `catalogo.db` (#167). Las migraciones siguientes las crean con `catalog.`.</summary>
    public static readonly string[] CatalogTables = ["console", "console_platform", "saga", "game", "version", "version_criterion",
        "field_value", "route", "route_step", "learning", "saga_rule", "claude_fingerprint"];

    /// <summary>Columnas propias que vivían en tablas del catálogo: pasan a `saga_user` (o se van, si nadie las usa).</summary>
    static readonly (string Table, string Column)[] PersonalColumns =
        [("saga", "favorite"), ("saga", "following"), ("saga", "chosen_route"), ("version", "export_to_steam")];

    /// <summary>
    /// Separa el catálogo de lo propio (#167): copia sus tablas a la base adjunta, verifica las filas, pasa favorita, sigo y ruta
    /// elegida a `saga_user` y recién entonces borra las de la base propia. Las tablas propias que apuntaban al catálogo se
    /// rehacen sin esa clave foránea (SQLite no las admite entre archivos). Antes deja una copia de la base, si tenía juegos.
    /// </summary>
    static void SplitCatalog(Database db, SqliteConnection c, string name)
    {
        var schema = c.Query<(string Type, string Name, string Table, string Sql)>(
            "SELECT type, name, tbl_name, sql FROM main.sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'").ToList();
        if (c.ExecuteScalar<long>("SELECT count(*) FROM main.game") > 0)
        {
            c.Execute("PRAGMA main.wal_checkpoint(TRUNCATE)");
            var backup = Path.ChangeExtension(db.FilePath, ".antes-del-catalogo.db");
            if (!File.Exists(backup)) File.Copy(db.FilePath, backup);
        }

        // Con las claves foráneas activas, borrar una tabla borra en cascada lo que apunta a ella.
        c.Execute("PRAGMA foreign_keys = OFF");
        try
        {
            // 1. El catálogo, en su archivo. Un intento anterior cortado a medias (la migración no quedó anotada) se rehace desde
            //    cero: hasta el paso 2, la base propia sigue entera.
            using (var t = c.BeginTransaction())
            {
                foreach (var table in CatalogTables.Append("catalog_migration")) c.Execute($"DROP TABLE IF EXISTS catalog.{table}", transaction: t);
                c.Execute("CREATE TABLE catalog.catalog_migration (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL)", transaction: t);
                foreach (var table in CatalogTables)
                {
                    var create = schema.Single(x => x.Type == "table" && x.Name == table).Sql;
                    c.Execute(Regex.Replace(create, @"^CREATE TABLE\s+""?(\w+)""?", "CREATE TABLE catalog.$1"), transaction: t);
                    c.Execute($"INSERT INTO catalog.{table} SELECT * FROM main.{table}", transaction: t);
                    var (from, to) = (c.ExecuteScalar<long>($"SELECT count(*) FROM main.{table}", transaction: t),
                        c.ExecuteScalar<long>($"SELECT count(*) FROM catalog.{table}", transaction: t));
                    if (from != to) throw new InvalidOperationException($"{table}: {from} filas, se copiaron {to}");
                }
                foreach (var index in schema.Where(x => x.Type == "index" && CatalogTables.Contains(x.Table)))
                    c.Execute(Regex.Replace(index.Sql, @"^CREATE (UNIQUE )?INDEX\s+(IF NOT EXISTS\s+)?", "CREATE $1INDEX $2catalog."), transaction: t);
                t.Commit();
            }

            // 2. Lo propio: solo escribe la base propia (lee el catálogo), así que es atómico. Las columnas propias que quedan en el
            //    catálogo las quita DropPersonalColumns al final de Apply.
            using var t2 = c.BeginTransaction();
            c.Execute("""
                CREATE TABLE main.saga_user (
                    saga_id      INTEGER PRIMARY KEY,
                    favorite     INTEGER NOT NULL DEFAULT 0,
                    following    INTEGER NOT NULL DEFAULT 0,
                    chosen_route INTEGER
                );
                INSERT INTO main.saga_user SELECT id, favorite, following, chosen_route FROM catalog.saga
                WHERE favorite <> 0 OR following <> 0 OR chosen_route IS NOT NULL;
                """, transaction: t2);
            foreach (var table in CatalogTables) c.Execute($"DROP TABLE main.{table}", transaction: t2);

            // Lo propio que apuntaba al catálogo: se rehace sin esa clave foránea, con sus índices.
            var toCatalog = $@"\s+REFERENCES\s+""?({string.Join('|', CatalogTables)})""?\s*\([^)]*\)(\s+ON\s+(DELETE|UPDATE)\s+(SET\s+NULL|SET\s+DEFAULT|CASCADE|RESTRICT|NO\s+ACTION))*";
            foreach (var (_, table, _, sql) in schema.Where(x => x.Type == "table" && !CatalogTables.Contains(x.Name) && Regex.IsMatch(x.Sql, toCatalog)))
            {
                var columns = string.Join(", ", c.Query<string>($"SELECT name FROM pragma_table_info('{table}', 'main')", transaction: t2));
                c.Execute($"ALTER TABLE main.{table} RENAME TO {table}_old", transaction: t2);
                c.Execute(Regex.Replace(Regex.Replace(sql, toCatalog, ""), @"^CREATE TABLE\s+""?(\w+)""?", "CREATE TABLE main.$1"), transaction: t2);
                c.Execute($"INSERT INTO main.{table} ({columns}) SELECT {columns} FROM main.{table}_old; DROP TABLE main.{table}_old", transaction: t2);
                foreach (var index in schema.Where(x => x.Type == "index" && x.Table == table))
                    c.Execute(Regex.Replace(index.Sql, @"^CREATE (UNIQUE )?INDEX\s+(IF NOT EXISTS\s+)?", "CREATE $1INDEX $2main."), transaction: t2);
            }

            Done(c, name, t2);
            t2.Commit();
        }
        finally
        {
            c.Execute("PRAGMA foreign_keys = ON");
        }
    }

    /// <summary>
    /// Las bases creadas con F0 llevan el registro de migraciones en `migracion (nombre, aplicada)`.
    /// Se renombra antes de leerlo; los nombres de las migraciones ya aplicadas no cambian (#139).
    /// </summary>
    static void RenameLegacyLog(SqliteConnection c)
    {
        if (c.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'migracion'") == 0) return;
        using var t = c.BeginTransaction();
        c.Execute("""
            ALTER TABLE migracion RENAME TO migration;
            ALTER TABLE migration RENAME COLUMN nombre TO name;
            ALTER TABLE migration RENAME COLUMN aplicada TO applied_at;
            """, transaction: t);
        t.Commit();
    }

    /// <summary>Registra o actualiza este equipo (lo que depende de cada PC se guarda por equipo, §7.6).</summary>
    public static void RegisterMachine(Database db, string id)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO machine (id, name, os) VALUES (@id, @name, @os)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, os = excluded.os
            """, new { id, name = Environment.MachineName, os = RuntimeInformation.OSDescription });
    }
}

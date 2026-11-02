using System.IO.Compression;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Lo que entra y sale del catálogo como archivo. Lo que no esté en <see cref="Shared"/> no se publica: una tabla nueva queda fuera hasta decidirlo.
/// </summary>
public static class InitialCatalog
{
    /// <summary>Lo que se publica, con cada tabla antes de las que apuntan a ella. `claude_fingerprint` evita que Claude vuelva a juzgar lo ya juzgado: son hashes.</summary>
    static readonly string[] Shared = ["console", "console_platform", "saga", "game", "version", "version_criterion", "field_value", "route", "route_step", "claude_fingerprint"];

    /// <summary>Escribe una copia comprimida del catálogo, sin nada propio: ni tus correcciones a mano, ni tus reglas para Claude, ni qué consolas habilitaste.</summary>
    public static void Export(Database db, string path)
    {
        var temp = path + ".tmp";
        File.Delete(temp);
        using (var c = db.Open()) c.Execute("VACUUM catalog INTO @temp", new { temp });
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
        {
            copy.Open();
            foreach (var table in copy.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ToList())
                if (!Shared.Contains(table)) copy.Execute($"DROP TABLE \"{table}\"");
            copy.Execute("""
                DELETE FROM field_value WHERE manual = 1;
                UPDATE console SET enabled = 1, refreshed_at = NULL;
                VACUUM;
                """);
        }
        using (var input = File.OpenRead(temp))
        using (var output = new GZipStream(File.Create(path), CompressionLevel.SmallestSize))
            input.CopyTo(output);
        File.Delete(temp);
    }

    /// <summary>Lo que se conserva al traer un catálogo existente, además de lo publicado: el historial y las reglas de Claude son del generador.</summary>
    static readonly string[] Editor = [.. Shared, "learning", "saga_rule"];

    /// <summary>
    /// Trae una copia de un `catalogo.db` existente a este catálogo vacío. Es el punto de partida del generador; nunca lee la base personal de nadie.
    /// Todo o nada, y solo con las columnas que existen en los dos lados.
    /// </summary>
    /// <returns>Si lo trajo; false si este catálogo ya tiene juegos.</returns>
    public static bool Import(Database db, string file)
    {
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT count(*) FROM catalog.game") > 0) return false;
        c.Execute("ATTACH DATABASE @file AS initial", new { file });
        try
        {
            using var t = c.BeginTransaction();
            foreach (var table in Editor)
            {
                var columns = string.Join(", ", c.Query<string>($"SELECT name FROM pragma_table_info('{table}', 'catalog') WHERE name IN (SELECT name FROM pragma_table_info('{table}', 'initial'))", transaction: t));
                // Las consolas ya vienen de las migraciones.
                if (columns.Length > 0) c.Execute($"INSERT OR IGNORE INTO catalog.{table} ({columns}) SELECT {columns} FROM initial.{table}", transaction: t);
            }
            t.Commit();
        }
        finally { c.Execute("DETACH DATABASE initial"); }
        return true;
    }
}

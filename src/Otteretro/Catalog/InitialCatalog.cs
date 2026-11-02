using Dapper;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Traer un catálogo existente al generador.
/// </summary>
public static class InitialCatalog
{
    /// <summary>Lo que se trae, con cada tabla antes de las que apuntan a ella. `claude_fingerprint` evita que Claude vuelva a juzgar lo ya juzgado.</summary>
    static readonly string[] Shared = ["console", "console_platform", "saga", "game", "version", "version_criterion", "field_value", "route", "route_step", "claude_fingerprint"];

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

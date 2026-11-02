using Dapper;
using Microsoft.Data.Sqlite;

namespace Otteretro.Data;

/// <summary>
/// La base SQLite en modo WAL: la app, el lanzador y la CLI pueden usarla a la vez. Son dos archivos (#167): lo propio en
/// <see cref="FilePath"/> y el catálogo (sagas, juegos, versiones…) en <see cref="CatalogPath"/>, para poder distribuirlo sin
/// datos de la persona. El catálogo va adjunto como `catalog`; las tablas tienen nombres únicos, así que las consultas no
/// necesitan prefijo.
/// </summary>
/// <param name="catalogPath">Sin él, `catalogo.db` junto a <paramref name="filePath"/> (para las pruebas).</param>
public sealed class Database(string filePath, string? catalogPath = null)
{
    public string FilePath { get; } = filePath;
    public string CatalogPath { get; } = catalogPath ?? Path.Combine(Path.GetDirectoryName(filePath)!, "catalogo.db");

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        // Las conexiones vuelven del pool con el catálogo ya adjunto.
        if (connection.ExecuteScalar<long>("SELECT count(*) FROM pragma_database_list WHERE name = 'catalog'") == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            connection.Execute("ATTACH DATABASE @path AS catalog", new { path = CatalogPath });
        }
        connection.Execute("PRAGMA journal_mode=WAL; PRAGMA catalog.journal_mode=WAL; PRAGMA busy_timeout=5000;");
        return connection;
    }
}

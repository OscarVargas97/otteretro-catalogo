using Dapper;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Tests;

public class DataTests
{
    [Fact]
    public void MigrationsAreIdempotent()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp["otteretro.db"]);
        Assert.True(Migrations.Apply(db) > 0);
        Assert.Equal(0, Migrations.Apply(db));

        using var c = db.Open();
        Assert.Equal("wal", c.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE name = 'machine'"));
    }

    /// <summary>
    /// F2.20 (#92): una base de antes se separa al actualizar. El catálogo queda en su archivo sin nada propio (tabla por tabla),
    /// lo propio sigue en la base de la persona con sus filas, favorita y sigo pasan a saga_user y queda una copia de antes.
    /// </summary>
    [Fact]
    public void TheCatalogIsSplitFromYourDataWithNothingOfYours()
    {
        using var tmp = new TempDir();
        var folder = tmp["datos"];
        DataFolder.Prepare(folder);
        var db = DataFolder.Open(folder);
        Migrations.Apply(db, until: "0012_console_platform.sql");
        Migrations.RegisterMachine(db, "pc");
        using (var c = db.Open())
            c.Execute("""
                INSERT INTO main.saga (id, name, favorite, following) VALUES (1, 'Metroid', 1, 1), (2, 'Halo', 0, 0);
                INSERT INTO main.game (id, saga_id, name_es) VALUES (10, 1, 'Metroid'), (20, 2, 'Halo');
                INSERT INTO main.version (id, game_id, platform) VALUES (100, 10, 'nes'), (200, 20, 'xbox');
                INSERT INTO main.field_value (entity, entity_id, field, value, source) VALUES ('game', 10, 'year', '1986', 'igdb');
                INSERT INTO main.progress (game_id, status) VALUES (10, 'finished');
                INSERT INTO main.source (kind, store, store_id, name, version_id) VALUES ('store', 'steam', '1', 'Halo', 200);
                """);

        Assert.Equal(1, Migrations.Apply(db, until: "0013_catalog.sql"));

        Assert.True(File.Exists(Path.ChangeExtension(db.FilePath, ".antes-del-catalogo.db")));
        using (var c = db.Open())
        {
            Assert.Equal(0, c.ExecuteScalar<long>($"SELECT count(*) FROM main.sqlite_master WHERE type = 'table' AND name IN ({string.Join(',', Migrations.CatalogTables.Select(t => $"'{t}'"))})"));
            Assert.Equal((1L, 1L), c.QuerySingle<(long, long)>("SELECT favorite, following FROM saga_user WHERE saga_id = 1"));
            Assert.Equal("finished", c.ExecuteScalar<string>("SELECT status FROM progress WHERE game_id = 10"));
            Assert.Equal("Halo", c.ExecuteScalar<string>("SELECT g.name_es FROM source o JOIN version v ON v.id = o.version_id JOIN game g ON g.id = v.game_id"));
            Assert.Equal("1986", c.ExecuteScalar<string>("SELECT value FROM field_value WHERE entity_id = 10"));
            Assert.Empty(c.Query("PRAGMA main.foreign_key_check"));
            Assert.Empty(c.Query("PRAGMA catalog.foreign_key_check"));
            // Borrar un juego del catálogo ya no borra en cascada tu progreso (SQLite no las admite entre archivos).
            c.Execute("DELETE FROM version WHERE game_id = 10; DELETE FROM game WHERE id = 10");
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT count(*) FROM progress"));
        }

        // El catálogo solo: sus tablas y ninguna columna propia.
        using (var cat = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.CatalogPath}"))
        {
            cat.Open();
            var tables = cat.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ToList();
            Assert.All(tables, t => Assert.Contains(t, Migrations.CatalogTables.Append("catalog_migration")));
            var columns = tables.SelectMany(t => cat.Query<string>($"SELECT name FROM pragma_table_info('{t}')")).ToList();
            Assert.DoesNotContain("favorite", columns);
            Assert.DoesNotContain("following", columns);
            Assert.DoesNotContain("chosen_route", columns);
            Assert.DoesNotContain("export_to_steam", columns);
            Assert.Equal(2, cat.ExecuteScalar<long>("SELECT count(*) FROM saga"));
        }
        Assert.True(Migrations.Apply(db) > 0); // las que siguen se aplican sobre el catálogo separado
        Assert.Equal(0, Migrations.Apply(db));
    }

    /// <summary>
    /// Revisión de F2: un corte después de copiar el catálogo y antes de terminar deja la migración sin anotar. Al arrancar de
    /// nuevo se rehace sin fallar ni perder nada, y las columnas propias que hayan quedado en el catálogo se quitan solas.
    /// </summary>
    [Fact]
    public void AnInterruptedSplitIsRedoneOnTheNextStart()
    {
        using var tmp = new TempDir();
        var folder = tmp["datos"];
        DataFolder.Prepare(folder);
        var db = DataFolder.Open(folder);
        Migrations.Apply(db, until: "0012_console_platform.sql");
        using (var c = db.Open())
            c.Execute("""
                INSERT INTO main.saga (id, name, favorite) VALUES (1, 'Metroid', 1);
                CREATE TABLE catalog.saga (id INTEGER PRIMARY KEY, name TEXT, favorite INTEGER); -- lo que dejó el corte
                INSERT INTO catalog.saga VALUES (1, 'Metroid', 1);
                """);

        Assert.True(Migrations.Apply(db) > 0);

        using var after = db.Open();
        Assert.Equal("Metroid", after.ExecuteScalar<string>("SELECT name FROM saga WHERE id = 1"));
        Assert.Equal(1, after.ExecuteScalar<long>("SELECT favorite FROM saga_user WHERE saga_id = 1"));
        Assert.Equal(0, after.ExecuteScalar<long>("SELECT count(*) FROM pragma_table_info('saga', 'catalog') WHERE name = 'favorite'"));
    }

    /// <summary>F1.4: las 24 plataformas quedan cargadas y la saga no lleva plataforma (#131).</summary>
    [Fact]
    public void CatalogHasThe24Platforms()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp["otteretro.db"]);
        Migrations.Apply(db);
        using var c = db.Open();
        Assert.Equal(26, c.ExecuteScalar<int>("SELECT count(*) FROM console"));
        Assert.Equal(24, c.ExecuteScalar<int>("SELECT count(*) FROM console WHERE company IS NOT NULL"));
        Assert.Equal(26, c.ExecuteScalar<int>("SELECT count(*) FROM console WHERE enabled = 1")); // F2.13
        Assert.Equal("switch", c.ExecuteScalar<string>("SELECT console_id FROM console_platform WHERE igdb_id = 130"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('saga') WHERE name = 'platform'"));

        // Una saga con versiones de PC y de consola, sin duplicar la saga.
        c.Execute("""
            INSERT INTO saga (id, name) VALUES (1, 'Half-Life');
            INSERT INTO game (id, saga_id, name_es) VALUES (1, 1, 'Half-Life');
            INSERT INTO version (game_id, platform) VALUES (1, 'pc'), (1, 'ps2');
            INSERT INTO progress (game_id, status, rating) VALUES (1, 'finished', 9);
            """);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => c.Execute("INSERT INTO version (game_id, platform) VALUES (1, 'dreamcast')"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => c.Execute("UPDATE progress SET rating = 11 WHERE game_id = 1"));
    }

    [Fact]
    public void RegisteringTheMachineTwiceDoesNotDuplicate()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp["otteretro.db"]);
        Migrations.Apply(db);
        Migrations.RegisterMachine(db, "pc-1");
        Migrations.RegisterMachine(db, "pc-1");
        using var c = db.Open();
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT count(*) FROM machine"));
    }

    /// <summary>Una base tal como la dejaba F0 (tablas en español) se actualiza sin perder lo que tenía (#139).</summary>
    [Fact]
    public void DatabaseFromF0IsUpgraded()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp["otteretro.db"]);
        using (var c = db.Open())
            c.Execute("""
                CREATE TABLE migracion (nombre TEXT PRIMARY KEY, aplicada TEXT NOT NULL);
                CREATE TABLE equipo (id TEXT PRIMARY KEY, nombre TEXT NOT NULL, so TEXT NOT NULL);
                CREATE TABLE ajuste (clave TEXT NOT NULL, valor TEXT NOT NULL, equipo_id TEXT NOT NULL DEFAULT '', PRIMARY KEY (clave, equipo_id));
                INSERT INTO migracion VALUES ('0001_equipo.sql', '2026-10-01T00:00:00Z');
                INSERT INTO equipo VALUES ('pc-viejo', 'PC', 'Windows');
                INSERT INTO ajuste VALUES ('tema', 'oscuro', '');
                """);

        // Se aplican todas menos la 0001, que ya estaba.
        var total = typeof(Migrations).Assembly.GetManifestResourceNames().Count(r => r.EndsWith(".sql", StringComparison.Ordinal));
        Assert.Equal(total - 1, Migrations.Apply(db));

        using var after = db.Open();
        Assert.Equal("PC", after.ExecuteScalar<string>("SELECT name FROM machine WHERE id = 'pc-viejo'"));
        Assert.Equal("oscuro", after.ExecuteScalar<string>("SELECT value FROM setting WHERE name = 'tema' AND machine_id = ''"));
        Assert.Equal(total, after.ExecuteScalar<int>("SELECT (SELECT count(*) FROM migration) + (SELECT count(*) FROM catalog.catalog_migration)")); // lo del catálogo se anota en él
        Assert.Equal(0, after.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE name IN ('migracion', 'equipo', 'ajuste')"));
    }

    [Fact]
    public void DataFolderCreatesItsSubfolders()
    {
        using var tmp = new TempDir();
        var path = tmp["Otteretro"];
        Assert.Null(DataFolder.Prepare(path));
        Assert.True(DataFolder.IsReady(path));
        Assert.True(Directory.Exists(Path.Combine(path, "catalogo", "imagenes")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relativa\\carpeta")]
    public void DataFolderRejectsPartialPaths(string? path) =>
        Assert.NotNull(DataFolder.Prepare(path));

    [Fact]
    public void DataFolderRejectsAFile()
    {
        using var tmp = new TempDir();
        var file = tmp["archivo.txt"];
        File.WriteAllText(file, "x");
        Assert.NotNull(DataFolder.Prepare(file));
    }

    [Fact]
    public void ConfigIsSavedAndLoaded()
    {
        using var tmp = new TempDir();
        var path = tmp["config.json"];
        var c = AppConfig.Load(path);
        c.DataFolder = "E:\\Otteretro";
        c.Save(path);
        var loaded = AppConfig.Load(path);
        Assert.Equal("E:\\Otteretro", loaded.DataFolder);
        Assert.Equal(c.MachineId, loaded.MachineId);
    }

    /// <summary>#59: un config.json sin equipoId (escrito a mano) guarda el que se inventa, para que las claves se sigan encontrando.</summary>
    [Fact]
    public void ConfigWithoutMachineIdKeepsTheSameOne()
    {
        using var tmp = new TempDir();
        var path = tmp["config.json"];
        File.WriteAllText(path, """{ "carpetaDatos": "E:\\Otteretro" }""");
        Assert.Equal(AppConfig.Load(path).MachineId, AppConfig.Load(path).MachineId);
        Assert.Equal("E:\\Otteretro", AppConfig.Load(path).DataFolder);
    }

    /// <summary>El config.json que escribía F0 se sigue leyendo: sus claves no cambian.</summary>
    [Fact]
    public void ConfigFromF0IsRead()
    {
        using var tmp = new TempDir();
        var path = tmp["config.json"];
        File.WriteAllText(path, """{ "carpetaDatos": "E:\\Otteretro", "origenActualizaciones": "D:\\releases", "equipoId": "pc-viejo" }""");
        var c = AppConfig.Load(path);
        Assert.Equal("E:\\Otteretro", c.DataFolder);
        Assert.Equal("pc-viejo", c.MachineId);
        // Sin `language`, sigue al sistema (#140).
        Assert.Null(c.Language);
        Assert.Equal(AppConfig.SystemLanguage(), c.EffectiveLanguage);
    }

    [Fact]
    public void UnknownLanguageFallsBackToTheSystem()
    {
        var c = new AppConfig { Language = "xx" };
        Assert.Contains(c.EffectiveLanguage, AppConfig.Languages);
    }
}

/// <summary>#69: una base que ya aplicó la 0028 de ports conserva sus filas al pasar por la 0029 (no-port y delisted).</summary>
public class PortMigrationTests
{
    [Fact]
    public void ThePortTableKeepsItsRowsAndGainsTheNewColumns()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp["otteretro.db"]);
        Migrations.Apply(db, until: "0028_port.sql");
        using (var c = db.Open())
            c.Execute("""
                INSERT INTO port (id, console, game, project, kind, status, link, source, added, emulator, emulator_level, game_id, manual)
                VALUES (7, 'n64', 'Super Mario 64', 'sm64coopdx', 'decomp', 'complete', 'https://github.com/coop-deluxe/sm64coopdx', 'extra', '2026-10-14', 'ares', NULL, 42, 1)
                """);

        Assert.Equal(3, Migrations.Apply(db)); // la 0029, la 0030 (recetas, #94) y la 0031 (conflictos, #104)

        using var after = db.Open();
        Assert.Equal(("sm64coopdx", "complete", 42L, true, false), after.QuerySingle<(string, string, long, bool, bool)>(
            "SELECT project, status, game_id, manual, delisted FROM port WHERE id = 7"));
        after.Execute("""
            INSERT INTO port (console, game, project, kind, status, link, source, added, delisted)
            VALUES ('n64', 'Super Mario 64', 'sm64', 'decomp', 'no-port', 'https://github.com/n64decomp/sm64', 'recompendium', '2026-11-05', 1)
            """);
        Assert.Equal(1, after.ExecuteScalar<long>("SELECT count(*) FROM catalog.sqlite_master WHERE type = 'index' AND name = 'port_game'"));
    }
}

/// <summary>
/// Un cambio largo del catálogo (una página de IGDB) bloquea también la base propia: en una conexión con el catálogo adjunto, toda
/// transacción toma la escritura de los dos archivos. Las demás tareas esperan a que termine en vez de fallar con «database is locked».
/// </summary>
public class LockTests
{
    [Fact]
    public async Task AWriterWaitsForALongCatalogTransaction()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp["otteretro.db"]);
        Migrations.Apply(db);
        var hold = TimeSpan.FromSeconds(6); // más que los 5 s de antes
        var started = new TaskCompletionSource();
        var writer = Task.Run(() =>
        {
            using var c = db.Open();
            using var t = c.BeginTransaction();
            c.Execute("INSERT INTO catalog.saga (name) VALUES ('Larga')", transaction: t);
            started.SetResult();
            Thread.Sleep(hold);
            t.Commit();
        });
        await started.Task;
        using (var other = db.Open())
            other.Execute("INSERT INTO task (kind, status, progress, created_at) VALUES ('demo', 'queued', 0, '2026-11-05')"); // la base propia
        await writer;
    }
}

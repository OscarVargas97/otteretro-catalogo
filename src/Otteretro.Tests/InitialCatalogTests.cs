using System.IO.Compression;
using System.Text;
using Dapper;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#211: el catálogo entra y sale como archivo, sin nada de la persona.</summary>
[Collection("Dats.Handler")]
public class InitialCatalogTests
{
    const string Canary = "DATO-PERSONAL-DE-PRUEBA";

    static void Fill(Fixture f)
    {
        using var c = f.Db.Open();
        c.Execute($"""
            INSERT INTO catalog.saga (id, name) VALUES (1, 'Metroid');
            INSERT INTO catalog.game (id, saga_id, name_es) VALUES (10, 1, 'Metroid Fusion');
            INSERT INTO catalog.version (id, game_id, platform) VALUES (20, 10, 'gba');
            INSERT INTO catalog.field_value (entity, entity_id, field, value, source, manual) VALUES ('game', 10, 'year', '2002', 'igdb', 0), ('game', 10, 'notes', '{Canary}', 'manual', 1);
            INSERT INTO catalog.learning (kind, content, created_at) VALUES ('rule', '{Canary}', '2026-01-01');
            UPDATE catalog.console SET enabled = 0, refreshed_at = '{Canary}' WHERE id = 'gba';
            CREATE TABLE catalog.something_new (value TEXT);
            INSERT INTO catalog.something_new VALUES ('{Canary}');
            INSERT INTO main.progress (game_id, status, notes) VALUES (10, 'finished', '{Canary}');
            INSERT INTO main.download_link (url, name, package, created_at) VALUES ('https://sitio.test/{Canary}', '{Canary}', '{Canary}', '2026-01-01');
            """);
    }

    static byte[] Plain(string gz)
    {
        using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public async Task TheExportHasNothingPersonal()
    {
        await using var f = new Fixture();
        Fill(f);
        var file = f.Tmp["catalogo-inicial.db.gz"];
        InitialCatalog.Export(f.Db, file);
        var text = Encoding.UTF8.GetString(Plain(file));
        Assert.Contains("Metroid Fusion", text);
        Assert.DoesNotContain(Canary, text);
        Assert.False(File.Exists(file + ".tmp"));
    }

    /// <summary>Traer un catalogo.db existente conserva el historial del generador; un catálogo con juegos no se toca.</summary>
    [Fact]
    public async Task ImportingFillsAnEmptyCatalogOnly()
    {
        await using var source = new Fixture();
        Fill(source);
        var file = source.Tmp["copia.db"];
        using (var c = source.Db.Open()) c.Execute("VACUUM catalog INTO @file", new { file });

        await using var fresh = new Fixture();
        Assert.True(InitialCatalog.Import(fresh.Db, file));
        using (var c = fresh.Db.Open())
        {
            Assert.Equal("Metroid Fusion", c.ExecuteScalar<string>("SELECT name_es FROM catalog.game WHERE id = 10"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT count(*) FROM catalog.learning"));
        }
        Assert.False(InitialCatalog.Import(fresh.Db, file));
    }
}

using System.IO.Compression;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#3: el catálogo se publica como archivos, se valida y se compila en la base que baja la app.</summary>
public class PublishedTests
{
    static void Fill(Fixture f)
    {
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO catalog.saga (id, name) VALUES (33, 'Final Fantasy');
            INSERT INTO catalog.game (id, saga_id, name_es, year) VALUES (10, 33, 'Final Fantasy X', 2001), (11, NULL, 'Tetris', 1984);
            INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (20, 10, 'ps2', 'original'), (21, 11, 'gb', 'original');
            INSERT INTO catalog.field_value (entity, entity_id, field, value, source, manual) VALUES ('game', 10, 'year', '2001', 'manual', 1);
            INSERT INTO catalog.route (id, saga_id, kind, approved) VALUES (1, 33, 'release', 1);
            INSERT INTO catalog.route_step (route_id, position, game_id, recommended_version_id, reason) VALUES (1, 1, 10, 20, 'El mejor punto de partida');
            INSERT INTO catalog.replaced (kind, old_id, new_id) VALUES ('game', 99, 10);
            INSERT INTO catalog.learning (kind, content, created_at) VALUES ('rule', 'REGLA-INTERNA', '2026-01-01');
            INSERT INTO catalog.image (game_id, kind, url) VALUES (10, 'cover', 'https://cdn2.steamgriddb.com/grid/ffx.png'), (77, 'cover', 'https://cdn2.steamgriddb.com/grid/borrado.png');
            """);
    }

    static string Written(Fixture f)
    {
        var folder = f.Tmp["publicado"];
        Published.Write(f.Db, folder);
        return folder;
    }

    [Fact]
    public async Task EachSagaIsAFileAndWritingTwiceGivesTheSameBytes()
    {
        await using var f = new Fixture();
        Fill(f);
        var folder = Written(f);
        var file = Path.Combine(folder, "sagas", "00033-final-fantasy.json");
        var first = File.ReadAllText(file);
        Assert.Contains("\"nameEs\": \"Final Fantasy X\"", first);
        Assert.DoesNotContain('\r', first);
        Published.Write(f.Db, folder);
        Assert.Equal(first, File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "sin-saga")));
    }

    [Fact]
    public async Task WhatIsPublishedIsValidAndCompilesWithTheSameIdsAndNothingInternal()
    {
        await using var f = new Fixture();
        Fill(f);
        var folder = Written(f);
        Assert.Empty(Published.Validate(folder));

        var output = f.Tmp["catalogo.db.gz"];
        var manifest = Published.Compile(folder, output);
        Assert.Equal((1, 2, 2), (manifest.Sagas, manifest.Games, manifest.Versions));
        Assert.Equal(64, manifest.Sha256.Length);

        var plain = f.Tmp["abierta.db"];
        using (var gz = new GZipStream(File.OpenRead(output), CompressionMode.Decompress))
        using (var file = File.Create(plain))
            gz.CopyTo(file);
        using (var c = new SqliteConnection($"Data Source={plain};Pooling=False"))
        {
            c.Open();
            Assert.Equal("Final Fantasy X", c.ExecuteScalar<string>("SELECT name_es FROM game WHERE id = 10 AND saga_id = 33"));
            Assert.Null(c.ExecuteScalar<long?>("SELECT saga_id FROM game WHERE id = 11"));
            Assert.Equal(20, c.ExecuteScalar<long>("SELECT recommended_version_id FROM route_step WHERE game_id = 10"));
            Assert.Equal(10, c.ExecuteScalar<long>("SELECT new_id FROM replaced WHERE old_id = 99"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT manual FROM field_value WHERE entity_id = 10"));
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM learning"));
            // #6: la referencia viaja; la de un juego que ya no existe, no.
            Assert.Equal("https://cdn2.steamgriddb.com/grid/ffx.png", c.ExecuteScalar<string>("SELECT url FROM image WHERE game_id = 10 AND kind = 'cover'"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT count(*) FROM image"));
        }
    }

    [Theory]
    [InlineData("\"platform\": \"ps2\"", "\"platform\": \"nada\"", "consola que no existe")]
    [InlineData("\"kind\": \"original\"", "\"kind\": \"inventado\"", "tipo desconocido")]
    [InlineData("\"gameId\": 10", "\"gameId\": 12345", "juego que no existe")]
    [InlineData("\"recommendedVersionId\": 20", "\"recommendedVersionId\": 12345", "versión que no existe")]
    [InlineData("\"nameEs\": \"Final Fantasy X\"", "\"nameEs\": \" \"", "no tiene nombre")]
    [InlineData("\"id\": 20", "\"id\": 21", "repetido")]
    [InlineData("https://cdn2.steamgriddb.com/grid/ffx.png", "http://sitio.test/x.png", "no es de SteamGridDB ni de IGDB")]
    [InlineData("\"cover\": \"https", "\"fondo\": \"https", "imagen de tipo desconocido")]
    public async Task InvalidDataDoesNotPass(string from, string to, string expected)
    {
        await using var f = new Fixture();
        Fill(f);
        var folder = Written(f);
        var file = Path.Combine(folder, "sagas", "00033-final-fantasy.json");
        var text = File.ReadAllText(file);
        Assert.Contains(from, text);
        File.WriteAllText(file, text.Replace(from, to));
        Assert.Contains(Published.Validate(folder), e => e.Contains(expected));
        Assert.Throws<InvalidDataException>(() => Published.Compile(folder, f.Tmp["no.gz"]));
    }

    [Fact]
    public async Task BrokenJsonAndABadReplacementSaySo()
    {
        await using var f = new Fixture();
        Fill(f);
        var folder = Written(f);
        var replaced = Path.Combine(folder, "reemplazados.json");
        File.WriteAllText(replaced, """[{ "kind": "game", "oldId": 10, "newId": 11 }, { "kind": "game", "oldId": 98, "newId": 12345 }]""");
        var errors = Published.Validate(folder);
        Assert.Contains(errors, e => e.Contains("sigue en el catálogo"));
        Assert.Contains(errors, e => e.Contains("no existe"));
        File.WriteAllText(replaced, "{ esto no es JSON");
        Assert.Contains(Published.Validate(folder), e => e.Contains("No se puede leer"));
    }
}

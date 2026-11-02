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

    /// <summary>La versión siguiente de <see cref="Fill"/>: renombra la saga, fusiona Tetris con un juego nuevo, quita una versión y suma un juego a la saga.</summary>
    static string Next(Fixture f, bool withReplacements = true)
    {
        using (var c = f.Db.Open())
        {
            c.Execute("PRAGMA foreign_keys = OFF"); // se borra como lo haría una edición: lo que apunte a Tetris no importa aquí
            c.Execute("""
                UPDATE catalog.saga SET name = 'Final Fantasy (serie)' WHERE id = 33;
                INSERT INTO catalog.game (id, saga_id, name_es, year) VALUES (12, 33, 'Final Fantasy XII', 2006), (13, NULL, 'Tetris DX', 1998);
                INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (22, 12, 'ps2', 'original'), (23, 13, 'gbc', 'original');
                DELETE FROM catalog.version WHERE game_id = 11; DELETE FROM catalog.game WHERE id = 11;
                """);
            if (withReplacements) c.Execute("INSERT INTO catalog.replaced (kind, old_id, new_id) VALUES ('game', 11, 13), ('version', 21, 23)");
        }
        var folder = f.Tmp[$"publicado-{Guid.NewGuid():N}"];
        Published.Write(f.Db, folder);
        return folder;
    }

    /// <summary>#31 (decisiones 260 y 262): lo que cambia entre dos versiones, por id, y el historial que lo acumula.</summary>
    [Fact]
    public async Task ChangesBetweenVersionsAreFoundByIdAndAccumulated()
    {
        await using var f = new Fixture();
        Fill(f);
        var before = Written(f);
        var after = Next(f);
        var changes = Published.Changes(before, after, "catalogo-2");
        Assert.Contains(changes, c => c is { Kind: "saga", Change: "renamed", Id: 33, From: "Final Fantasy", Name: "Final Fantasy (serie)" });
        Assert.Contains(changes, c => c is { Kind: "game", Change: "merged", Id: 11, Name: "Tetris", Into: 13 });
        Assert.Contains(changes, c => c is { Kind: "game", Change: "added", Id: 12 });
        Assert.All(changes, c => Assert.Equal("catalogo-2", c.Version));

        // La primera vez arranca desde la versión anterior; después solo suma.
        var first = Published.Extend(null, "catalogo-1", changes, "catalogo-2");
        Assert.Equal(["catalogo-1", "catalogo-2"], first.Versions);
        var file = f.Tmp["cambios.json"];
        Published.SaveHistory(file, first);
        var second = Published.Extend(Published.ReadHistory(file), "catalogo-2", [new("catalogo-3", "game", "renamed", 12, "FF XII", "Final Fantasy XII")], "catalogo-3");
        Assert.Equal(["catalogo-1", "catalogo-2", "catalogo-3"], second.Versions);
        Assert.Equal(changes.Count + 1, second.Entries.Count);
        Assert.Same(second, Published.Extend(second, "catalogo-3", [], "catalogo-3")); // publicar dos veces la misma no duplica
    }

    /// <summary>#44: los mismos datos dan el mismo hash de contenido aunque el archivo comprimido cambie; las imágenes cuentan como cambio.</summary>
    [Fact]
    public async Task SameDataSameContentAndImagesCountAsChanges()
    {
        await using var f = new Fixture();
        Fill(f);
        var before = Written(f);
        var (one, two) = (Published.Compile(before, f.Tmp["uno.gz"]), Published.Compile(before, f.Tmp["dos.gz"]));
        Assert.Equal(one.Content, two.Content);
        Assert.NotNull(one.Content);

        using (var c = f.Db.Open()) c.Execute("INSERT INTO catalog.image (game_id, kind, url) VALUES (11, 'cover', 'https://images.igdb.com/igdb/image/upload/t_cover_big/tetris.jpg')");
        var after = f.Tmp["con-imagen"];
        Published.Write(f.Db, after);
        Assert.NotEqual(one.Content, Published.Compile(after, f.Tmp["tres.gz"]).Content);
        Assert.Empty(Published.Changes(before, after, "v2")); // ni sagas ni juegos cambian…
        Assert.Equal(1, Published.ImageChanges(before, after)); // …pero un juego trae imagen
        Assert.Equal(1, Published.Extend(null, "v1", [], "v2", images: 1).Images!["v2"]);
    }

    /// <summary>#32 (decisión 261): un PR no puede perder un id sin reemplazo ni reutilizar uno que en main es otra cosa; lleva su resumen.</summary>
    [Fact]
    public async Task APullRequestCannotLoseOrReuseIds()
    {
        await using var f = new Fixture();
        Fill(f);
        var main = Written(f);
        var next = Next(f);
        Assert.Empty(Published.CheckPullRequest(main, next));

        await using var g = new Fixture();
        Fill(g);
        var lost = Published.CheckPullRequest(main, Next(g, withReplacements: false));
        Assert.Contains(lost, e => e.Contains("El juego 11") && e.Contains("reemplazados.json"));
        Assert.Contains(lost, e => e.Contains("La versión 21"));

        // El 99 ya se retiró en main (Fill lo reemplazó por el 10): no se puede volver a usar.
        await using var h = new Fixture();
        Fill(h);
        using (var c = h.Db.Open()) c.Execute("INSERT INTO catalog.game (id, saga_id, name_es) VALUES (99, NULL, 'Otro juego'); INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (99, 99, 'gb', 'original'); DELETE FROM catalog.replaced");
        var reused = h.Tmp["reusado"];
        Published.Write(h.Db, reused);
        Assert.Contains(Published.CheckPullRequest(main, reused), e => e.Contains("El juego 99") && e.Contains("retiró"));

        var summary = Published.Summary(Published.Changes(main, next, "pr"), lost);
        Assert.Contains("problemas que impiden fusionar", summary);
        Assert.Contains("**Fusionados: 1**", summary);
    }
}

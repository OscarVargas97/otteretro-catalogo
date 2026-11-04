using System.IO.Compression;
using System.Net;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#46: el TitleID o el serial de cada versión, de tablas públicas, se guarda, se publica y se compila.</summary>
public class GameCodesTests
{
    sealed class Tables : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            string? body = url switch
            {
                _ when url.Contains("titledb") => """
                    { "1": { "id": "01007EF00011E000", "name": "The Legend of Zelda™: Breath of the Wild" },
                      "2": { "id": "01007EF00011E001", "name": "The Legend of Zelda™: Breath of the Wild – The Master Trials" } }
                    """,
                _ when url.Contains("switchbrew") => "| 0400A3A01EE4C000 || Pokémon™ Pokopia || CHN || x\n",
                _ when url.EndsWith("Sony - PlayStation 2.dat") => """
                    game (
                        name "Grand Theft Auto III (USA)"
                        region "USA"
                        serial "SLUS-20062"
                        rom ( name "Grand Theft Auto III (USA).iso" size 1 crc 00 sha1 AA )
                    )
                    game (
                        name "Grand Theft Auto III (Europe)"
                        rom ( name "x.iso" size 1 crc 00 sha1 BB serial "SLES-50330" )
                    )
                    """,
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    [Fact]
    public async Task CodesComeFromPublicTablesAndArePublishedAndCompiled()
    {
        await using var f = new Fixture();
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO catalog.saga (id, name) VALUES (1, 'Zelda');
                INSERT INTO catalog.game (id, saga_id, name_es, original_name) VALUES (10, 1, 'The Legend of Zelda: Breath of the Wild', NULL),
                    (11, NULL, 'Grand Theft Auto III', NULL), (12, NULL, 'Pokémon Pokopia', NULL);
                INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (20, 10, 'switch', 'original'), (21, 11, 'ps2', 'original'), (22, 12, 'switch2', 'original');
                """);
        GameCodes.Handler = new Tables();
        try { Assert.Equal("done", (await f.Run(GameCodes.Enqueue(f.Db))).Status); }
        finally { GameCodes.Handler = null; }

        using (var c = f.Db.Open())
        {
            var codes = c.Query<(long, string)>("SELECT version_id, code FROM catalog.version_code ORDER BY version_id, code").ToList();
            // El DLC de BotW no se guarda como código del juego; las dos regiones de GTA III, sí.
            Assert.Equal([(20L, "01007EF00011E000"), (21L, "SLES-50330"), (21L, "SLUS-20062"), (22L, "0400A3A01EE4C000")], codes);
        }

        var folder = f.Tmp["publicado"];
        Published.Write(f.Db, folder);
        Assert.Contains("\"SLUS-20062\"", File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "sin-saga"))[0]));
        var output = f.Tmp["catalogo.db.gz"];
        Published.Compile(folder, output);
        var plain = f.Tmp["compilado.db"];
        using (var gz = new GZipStream(File.OpenRead(output), CompressionMode.Decompress))
        using (var file = File.Create(plain)) gz.CopyTo(file);
        using var compiled = new SqliteConnection($"Data Source={plain};Pooling=false");
        compiled.Open();
        Assert.Equal(4, compiled.ExecuteScalar<long>("SELECT count(*) FROM version_code"));
    }

    [Fact]
    public void NamesFromTablesMatchTheCatalogNames()
    {
        Assert.Equal("the legend of zelda ocarina of time", GameCodes.Normalize("Legend of Zelda, The - Ocarina of Time (USA) (Rev 1)"));
        Assert.Equal("splatoon 3", GameCodes.Normalize("Splatoon™ 3 – Nintendo Switch 2 Edition"));
    }
}

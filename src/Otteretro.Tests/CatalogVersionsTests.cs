using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Otteretro.App;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#104: bajar una versión publicada y fusionarla en tres vías con la base local, contra un GitHub falso.</summary>
public class CatalogVersionsTests
{
    const string Repo = GitHubPublisher.Repo, Tag = "catalogo-20261109-1200-abc1234", Commit = "abc1234ffffffffffffffffffffffffffffffff";

    sealed class FakeGitHub(byte[] baseZip, byte[] gz, string manifest) : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        /// <summary>El último commit de main (#106, decisión 373) y sus JSON.</summary>
        public string Head = "head0";
        public byte[] HeadZip = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = $"{request.Method} {Uri.UnescapeDataString(request.RequestUri!.PathAndQuery.TrimStart('/'))}";
            Calls.Add(call);
            object? body = call switch
            {
                $"GET repos/{Repo}/releases?per_page=100" => new object[]
                {
                    new { tag_name = "v1.2.3", published_at = "2026-11-01T00:00:00Z", assets = Array.Empty<object>() },
                    new
                    {
                        tag_name = Tag, published_at = "2026-11-09T12:00:00Z",
                        assets = new[] { new { name = "catalogo.db.gz", browser_download_url = "https://github.com/dl/catalogo.db.gz" },
                            new { name = "catalogo.db.gz.json", browser_download_url = "https://github.com/dl/catalogo.db.gz.json" },
                            new { name = "cambios.json", browser_download_url = "https://github.com/dl/cambios.json" } },
                    },
                },
                $"GET repos/{Repo}/commits/{Tag}" => new { sha = Commit },
                $"GET repos/{Repo}/git/ref/heads/main" => new { @object = new { sha = Head } },
                $"GET repos/{Repo}/zipball/base1" => baseZip,
                _ when call == $"GET repos/{Repo}/zipball/{Head}" => HeadZip,
                "GET dl/catalogo.db.gz" => gz,
                "GET dl/catalogo.db.gz.json" => Encoding.UTF8.GetBytes(manifest),
                // #105: el historial de cambios que muestra Ajustes › Catálogo.
                "GET dl/cambios.json" => Encoding.UTF8.GetBytes($$$"""{"versions":["{{{Tag}}}"],"entries":[{"version":"{{{Tag}}}","kind":"game","change":"added","id":13,"name":"Final Fantasy XII"}],"images":{"{{{Tag}}}":2}}"""),
                _ => throw new InvalidOperationException("Llamada inesperada: " + call),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }

    static byte[] Zip(string folder)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, "OscarVargas97-otteretro-catalogo-base1/catalogo/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
        return memory.ToArray();
    }

    static void Edit(string file, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(file))!;
        change(node);
        File.WriteAllText(file, node.ToJsonString());
    }

    [Fact]
    public async Task OnlyChangedThereComesInLocalStaysAndTheSameFieldIsAConflictUntilChosen()
    {
        await using var f = new Fixture();
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO catalog.saga (id, name) VALUES (33, 'Final Fantasy'), (40, 'Zelda');
                INSERT INTO catalog.game (id, saga_id, name_es, year) VALUES (10, 33, 'Final Fantasy X', 2001), (11, 40, 'Zelda', 1986), (12, NULL, 'Tetris', 1984);
                INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (20, 10, 'ps2', 'original'), (21, 11, 'nes', 'original'), (22, 12, 'gb', 'original');
                """);
        // La base: lo publicado en base1, de lo que parte el catálogo local (la primera vez, sin copia guardada: sus JSON).
        var baseFolder = f.Tmp["base"];
        Published.Write(f.Db, baseFolder);
        GitHubPublisher.SetBase(f.Db, "base1");

        // La versión: Tetris y Zelda cambian de año y entra un juego nuevo en Final Fantasy.
        var release = f.Tmp["version"];
        Published.Write(f.Db, release);
        Edit(Path.Combine(release, "sin-saga", "00000.json"), n => n["games"]![0]!["year"] = 1989);
        Edit(Path.Combine(release, "sagas", "00040-zelda.json"), n => n["games"]![0]!["year"] = 1987);
        Edit(Path.Combine(release, "sagas", "00033-final-fantasy.json"), n =>
        {
            var game = n["games"]![0]!.DeepClone();
            (game["id"], game["nameEs"], game["versions"]![0]!["id"]) = (13, "Final Fantasy XII", 23);
            n["games"]!.AsArray().Add(game);
        });
        var gzFile = f.Tmp["catalogo.db.gz"];
        var manifest = Published.Compile(release, gzFile);

        // Sin publicar: otro nombre para FF X y otro año para Zelda (el mismo campo que cambió la versión).
        using (var c = f.Db.Open())
            c.Execute("UPDATE catalog.game SET name_es = 'Final Fantasy X (local)' WHERE id = 10; UPDATE catalog.game SET year = 1990 WHERE id = 11;");

        var github = new FakeGitHub(Zip(baseFolder), File.ReadAllBytes(gzFile), System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var versions = new CatalogVersions(f.Db, new GitHubPublisher(f.Db, "secreto", github) { Work = f.Tmp["trabajo"] });

        var page = await versions.Page();
        Assert.Equal(("base1", 2L, 3L), (page.Base, page.Sagas, page.Games));
        var listed = Assert.Single(page.Versions);
        Assert.Equal((Tag, false, 2), (listed.Tag, listed.Current, listed.Images));
        Assert.Equal("Final Fantasy XII", Assert.Single(listed.Changes).Name);
        var result = await versions.Apply(Tag);

        Assert.Equal((Commit, 1), (result.Commit, result.Conflicts));
        using (var c = f.Db.Open())
        {
            Assert.Equal(1989, c.ExecuteScalar<long>("SELECT year FROM game WHERE id = 12"));
            Assert.Equal("Final Fantasy X (local)", c.ExecuteScalar<string>("SELECT name_es FROM game WHERE id = 10"));
            Assert.Equal(1990, c.ExecuteScalar<long>("SELECT year FROM game WHERE id = 11")); // nada se pisa sin elegir
            Assert.Equal("Final Fantasy XII", c.ExecuteScalar<string>("SELECT name_es FROM game WHERE id = 13"));
            Assert.Equal(13, c.ExecuteScalar<long>("SELECT game_id FROM version WHERE id = 23"));
            Assert.Equal(Commit, c.ExecuteScalar<string>("SELECT value FROM setting WHERE name = @BaseSetting AND machine_id = ''", new { GitHubPublisher.BaseSetting }));
        }
        var conflict = Assert.Single(CatalogVersions.Conflicts(f.Db));
        Assert.Equal(("game", """{"id":11}""", "year", "1986", "1990", "1987"), (conflict.Table, conflict.Key, conflict.Field, conflict.Base, conflict.Local, conflict.Remote));
        Assert.True((await versions.List()).Single().Current);

        // Con un conflicto sin elegir no se aplica otra versión.
        var pending = await Assert.ThrowsAsync<TaskException>(() => versions.Apply(Tag));
        Assert.Equal("publish.pendingConflicts", pending.Error.Code);

        Assert.True(CatalogVersions.Resolve(f.Db, conflict.Id, useRemote: true));
        Assert.Empty(CatalogVersions.Conflicts(f.Db));
        using (var c = f.Db.Open()) Assert.Equal(1987, c.ExecuteScalar<long>("SELECT year FROM game WHERE id = 11"));

        // Otra vez la misma versión: el origen es la copia guardada (no baja los JSON de nuevo) y no hay nada que cambiar.
        var zipballs = github.Calls.Count(x => x.Contains("zipball"));
        var again = await versions.Apply(Tag);
        Assert.Equal((0, 0, 0, 0), (again.Inserted, again.Updated, again.Deleted, again.Conflicts));
        Assert.Equal(zipballs, github.Calls.Count(x => x.Contains("zipball")));

        // #106 (decisión 373): main avanzó sin release nueva (otro año para Tetris); se fusiona sola y su commit queda como base.
        Edit(Path.Combine(release, "sin-saga", "00000.json"), n => n["games"]![0]!["year"] = 1990);
        (github.Head, github.HeadZip) = ("head1", Zip(release));
        var caught = await versions.CatchUp();
        Assert.Equal(("applied", "main@head1", 0), (caught.Status, caught.Tag, caught.Conflicts));
        using (var c = f.Db.Open())
        {
            Assert.Equal(1990, c.ExecuteScalar<long>("SELECT year FROM game WHERE id = 12"));
            Assert.Equal("Final Fantasy X (local)", c.ExecuteScalar<string>("SELECT name_es FROM game WHERE id = 10")); // lo no publicado se queda
        }
        Assert.Equal("current", (await versions.CatchUp()).Status);
        Assert.True((await versions.List()).Single().Current); // en el último commit de main, la release más nueva está en uso
    }

    [Fact]
    public async Task CatchUpIsQuietWithoutBaseOrOnTheLastCommitOfMain()
    {
        await using var f = new Fixture();
        var github = new FakeGitHub([], [], "{}");
        var versions = new CatalogVersions(f.Db, new GitHubPublisher(f.Db, "secreto", github) { Work = f.Tmp["trabajo"] });
        Assert.Equal("current", (await versions.CatchUp()).Status); // sin base no hay con qué fusionar
        github.Head = "base1";
        GitHubPublisher.SetBase(f.Db, "base1");
        Assert.Equal(("current", "main@base1"), ((await versions.CatchUp()).Status, (await versions.CatchUp()).Tag));
    }
}

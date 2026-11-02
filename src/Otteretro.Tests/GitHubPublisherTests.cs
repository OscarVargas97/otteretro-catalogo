using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#22: publicar una saga o un juego como PR, contra un GitHub falso.</summary>
public class GitHubPublisherTests
{
    const string Repo = GitHubPublisher.Repo, Fork = "alguien/otteretro-catalogo";
    static readonly string SagaFile = "catalogo/sagas/00033-final-fantasy.json";

    sealed class FakeGitHub(byte[] zip, bool canPush) : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public bool ForkExists, BranchExists, PrOpen;
        public string[] ChangedInMain = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery.TrimStart('/'));
            var call = $"{request.Method} {path}";
            Calls.Add(call);
            Assert.Equal("Bearer secreto", request.Headers.Authorization?.ToString());
            if (request.Content is not null) await request.Content.ReadAsStringAsync(ct);
            object? body = call switch
            {
                $"GET repos/{Repo}/git/ref/heads/main" => new { @object = new { sha = "head1" } },
                $"GET repos/{Repo}/zipball/head1" => zip,
                "GET user" => new { login = "alguien" },
                $"GET repos/{Repo}" => new { permissions = new { push = canPush } },
                $"GET repos/{Fork}" => ForkExists ? new { } : null,
                $"POST repos/{Repo}/forks" => Fork2(),
                $"POST repos/{Fork}/merge-upstream" => new { },
                $"GET repos/{Repo}/git/commits/head1" => new { tree = new { sha = "tree1" } },
                _ when call.StartsWith("POST ") && call.EndsWith("/git/trees") => new { sha = "tree2" },
                _ when call.StartsWith("POST ") && call.EndsWith("/git/commits") => new { sha = "commit2" },
                _ when call.StartsWith("GET ") && call.Contains("/git/ref/heads/catalogo/") => BranchExists ? new { } : null,
                _ when call.Contains("/git/refs") => new { },
                _ when call.StartsWith($"GET repos/{Repo}/pulls") => PrOpen ? new[] { new { html_url = "https://github.com/pr/1" } } : Array.Empty<object>(),
                $"POST repos/{Repo}/pulls" => new { html_url = "https://github.com/pr/2" },
                _ when call.StartsWith($"GET repos/{Repo}/compare/") => new { files = ChangedInMain.Select(f => new { filename = f }) },
                _ => throw new InvalidOperationException("Llamada inesperada: " + call),
            };
            if (body is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
        }

        object Fork2() { ForkExists = true; return new { }; }
    }

    /// <summary>El catálogo de main es el de la base antes de editar; el zip lo trae como lo da GitHub, dentro de «dueño-repo-sha».</summary>
    static byte[] MainZip(Fixture f)
    {
        var folder = f.Tmp["main"];
        Published.Write(f.Db, folder);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, "OscarVargas97-otteretro-catalogo-head1/catalogo/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
        return memory.ToArray();
    }

    static (Fixture F, FakeGitHub GitHub, GitHubPublisher Publisher) Setup(bool canPush, Action<Fixture>? edit = null)
    {
        GitHubPublisher.Poll = TimeSpan.FromMilliseconds(1);
        var f = new Fixture();
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO catalog.saga (id, name) VALUES (33, 'Final Fantasy'), (40, 'Zelda');
                INSERT INTO catalog.game (id, saga_id, name_es, year) VALUES (10, 33, 'Final Fantasy X', 2001), (11, 40, 'Zelda', 1986), (12, NULL, 'Tetris', 1984);
                INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (20, 10, 'ps2', 'original'), (21, 11, 'nes', 'original'), (22, 12, 'gb', 'original');
                """);
        var github = new FakeGitHub(MainZip(f), canPush);
        edit?.Invoke(f);
        return (f, github, new GitHubPublisher(f.Db, "secreto", github) { Work = f.Tmp["trabajo"] });
    }

    [Fact]
    public async Task ThePreviewShowsOnlyWhatChangedAndAMovedGameTouchesBothFiles()
    {
        var (f, github, publisher) = Setup(canPush: true, f =>
        {
            Assert.Null(Editing.RenameSaga(f.Db, 33, "Final Fantasy (principal)"));
            Assert.Null(Editing.MoveGame(f.Db, 11, 33)); // Zelda sale de su saga
        });
        await using var _ = f;
        var preview = await publisher.Preview(new("saga", 33));

        Assert.Empty(preview.Errors);
        Assert.False(preview.KnownBase);
        Assert.Equal(["sagas/00033-final-fantasy-principal.json", "sagas/00033-final-fantasy.json", "sagas/00040-zelda.json"], preview.Files.Select(x => x.Path));
        Assert.Equal(["added", "deleted", "modified"], preview.Files.Select(x => x.Change));
        Assert.Contains(preview.Files[2].Details, d => d.Code == "publish.change.gameRemoved");
        Assert.DoesNotContain(github.Calls, c => c.Contains("merge") || c.StartsWith("PUT"));
    }

    [Fact]
    public async Task WithPermissionTheBranchGoesToTheRepoAndAPrIsOpenedWithoutMerging()
    {
        var (f, github, publisher) = Setup(canPush: true, f => Assert.Null(Editing.SetIconic(f.Db, 33, true)));
        await using var _ = f;
        var result = await publisher.Publish(new("saga", 33));

        Assert.Equal(("https://github.com/pr/2", false), (result.Url, result.Updated));
        Assert.Contains($"POST repos/{Repo}/git/refs", github.Calls);
        Assert.Contains($"POST repos/{Repo}/pulls", github.Calls);
        Assert.DoesNotContain(github.Calls, c => c.Contains("/forks") || c.Contains("/merge") || c.StartsWith("PUT"));
    }

    [Fact]
    public async Task WithoutPermissionItForksAndOpensThePrFromTheFork()
    {
        var (f, github, publisher) = Setup(canPush: false, f => Assert.Null(Editing.DeleteGame(f.Db, 12)));
        await using var _ = f;
        await publisher.Publish(new("game", 12));

        Assert.Contains($"POST repos/{Repo}/forks", github.Calls);
        Assert.Contains($"POST repos/{Fork}/git/refs", github.Calls);
        Assert.Contains($"POST repos/{Repo}/pulls", github.Calls);
        Assert.DoesNotContain($"POST repos/{Repo}/git/refs", github.Calls);
    }

    [Fact]
    public async Task PublishingAgainUpdatesTheOpenPr()
    {
        var (f, github, publisher) = Setup(canPush: true, f => Assert.Null(Editing.SetIconic(f.Db, 33, true)));
        await using var _ = f;
        (github.BranchExists, github.PrOpen) = (true, true);
        var result = await publisher.Publish(new("saga", 33));

        Assert.Equal(("https://github.com/pr/1", true), (result.Url, result.Updated));
        Assert.Contains(github.Calls, c => c.StartsWith("PATCH ") && c.Contains("/git/refs/heads/catalogo/saga-33"));
        Assert.DoesNotContain($"POST repos/{Repo}/pulls", github.Calls);
    }

    [Fact]
    public async Task AFileChangedInMainAfterTheBaseIsNotOverwritten()
    {
        var (f, github, publisher) = Setup(canPush: true, f => Assert.Null(Editing.SetIconic(f.Db, 33, true)));
        await using var _ = f;
        GitHubPublisher.SetBase(f.Db, "base0");
        github.ChangedInMain = [SagaFile];

        var preview = await publisher.Preview(new("saga", 33));
        Assert.Equal([SagaFile], preview.Conflicts);
        var error = await Assert.ThrowsAsync<App.TaskException>(() => publisher.Publish(new("saga", 33)));
        Assert.Equal("publish.conflict", error.Error.Code);
        Assert.DoesNotContain(github.Calls, c => c.Contains("/git/trees"));
    }

    /// <summary>#41: «Publicar todo» lleva en un solo PR todo lo que cambió respecto de main, y el árbol se arma de a tandas.</summary>
    [Fact]
    public async Task PublishingEverythingTakesEveryChangedFileInOnePrBuiltInChunks()
    {
        var (f, github, publisher) = Setup(canPush: true, f =>
        {
            Assert.Null(Editing.SetIconic(f.Db, 33, true));
            using var c = f.Db.Open();
            // 250 sagas nuevas: más de una tanda (200 archivos por pedido).
            c.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 250) INSERT INTO catalog.saga (id, name) SELECT 1000 + i, 'Saga ' || i FROM n");
        });
        await using var _ = f;
        var preview = await publisher.Preview(new("all", 0));
        Assert.Equal(251, preview.Files.Count);
        Assert.Contains(preview.Files, x => x.Path == "sagas/00033-final-fantasy.json" && x.Change == "modified");
        Assert.DoesNotContain(preview.Files, x => x.Path == "sagas/00040-zelda.json"); // lo que no cambió no va

        await publisher.Publish(new("all", 0));
        Assert.Equal(2, github.Calls.Count(c => c == $"POST repos/{Repo}/git/trees"));
        Assert.Contains($"GET repos/{Repo}/git/ref/heads/catalogo/todo", github.Calls);
        Assert.Contains($"POST repos/{Repo}/pulls", github.Calls);
    }

    [Fact]
    public async Task NothingChangedIsNotPublished()
    {
        var (f, _, publisher) = Setup(canPush: true);
        await using var __ = f;
        Assert.Empty((await publisher.Preview(new("saga", 33))).Files);
        Assert.Equal("publish.nothingChanged", (await Assert.ThrowsAsync<App.TaskException>(() => publisher.Publish(new("saga", 33)))).Error.Code);
    }
}

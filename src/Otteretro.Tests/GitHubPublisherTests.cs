using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using System.Text.Json;
using Otteretro.Catalog;
using Otteretro.Claude;
using A = Otteretro.Claude.SagaAnalysis;

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
        /// <summary>Otros commits de main que se pueden bajar (la base local, #85), por sha.</summary>
        public readonly Dictionary<string, byte[]> Zips = [];

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
                _ when call.StartsWith($"GET repos/{Repo}/zipball/") => Zips[call.Split('/')[^1]],
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
    static byte[] MainZip(Fixture f, Action<string>? change = null)
    {
        var folder = f.Tmp[change is null ? "main" : "main-cambiado"];
        Published.Write(f.Db, folder);
        change?.Invoke(folder);
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

    /// <summary>
    /// #85: la base local es el commit de main del que parte el catálogo. Con main más adelante, la comparación es en tres vías: lo que
    /// cambió solo en main no se publica (lo revertiría) y solo es conflicto lo que cambió en los dos lados.
    /// </summary>
    static (Fixture F, FakeGitHub GitHub, GitHubPublisher Publisher) SetupAhead(Action<Fixture> edit)
    {
        var (f, github, publisher) = Setup(canPush: true);
        // base0 es lo de la fixture; main (head1) lo tiene con un cambio en Final Fantasy y otro en Zelda, que acá no se tocaron.
        github.Zips["base0"] = MainZip(f);
        var ahead = MainZip(f, folder =>
        {
            foreach (var file in new[] { "sagas/00033-final-fantasy.json", "sagas/00040-zelda.json" }) File.AppendAllText(Path.Combine(folder, file), "\n");
        });
        github = new FakeGitHub(ahead, canPush: true) { Zips = { ["base0"] = github.Zips["base0"] } };
        GitHubPublisher.SetBase(f.Db, "base0");
        edit(f);
        return (f, github, new GitHubPublisher(f.Db, "secreto", github) { Work = publisher.Work });
    }

    [Fact]
    public async Task OnlyWhatChangedOnBothSidesIsAConflict()
    {
        var (f, github, publisher) = SetupAhead(f => Assert.Null(Editing.SetIconic(f.Db, 33, true)));
        await using var _ = f;

        var preview = await publisher.Preview(new("all", 0));
        Assert.True(preview.KnownBase);
        Assert.Equal([SagaFile], preview.Conflicts);
        Assert.Equal(["sagas/00033-final-fantasy.json"], preview.Files.Select(x => x.Path)); // Zelda cambió solo en main: no va
        var error = await Assert.ThrowsAsync<App.TaskException>(() => publisher.Publish(new("all", 0)));
        Assert.Equal("publish.conflict", error.Error.Code);
        Assert.DoesNotContain(github.Calls, c => c.Contains("/git/trees") || c.Contains("/compare/"));
    }

    [Fact]
    public async Task WhatChangedOnlyHereIsPublishedAndWhatChangedOnlyInMainIsLeftAlone()
    {
        var (f, github, publisher) = SetupAhead(f => Assert.Null(Editing.DeleteGame(f.Db, 12)));
        await using var _ = f;

        var preview = await publisher.Preview(new("all", 0));
        Assert.Empty(preview.Conflicts);
        Assert.Empty(preview.Errors);
        Assert.Equal(["reemplazados.json", "sin-saga/00000.json"], preview.Files.Select(x => x.Path));
        await publisher.Publish(new("all", 0));
        Assert.Contains($"POST repos/{Repo}/pulls", github.Calls);
    }

    /// <summary>#94: las recetas de main no salen de la base local; Publicar todo no las borra.</summary>
    [Fact]
    public async Task PublishingEverythingLeavesTheRecipesOfMainAlone()
    {
        var (f, _, _) = Setup(canPush: true);
        await using var _f = f;
        var github = new FakeGitHub(MainZip(f, folder =>
        {
            Directory.CreateDirectory(Path.Combine(folder, Published.RecipesFolder));
            File.WriteAllText(Path.Combine(folder, Published.RecipesFolder, "dolphin.json"), "{}");
        }), canPush: true);
        Assert.Null(Editing.SetIconic(f.Db, 33, true));
        // #95: la de «Emuladores» (en la base local) sí va.
        var local = Directory.CreateDirectory(f.Tmp["recetas-locales"]).FullName;
        File.WriteAllText(Path.Combine(local, "mgba.json"), """{ "id": "mgba", "name": "mGBA", "kind": "emulator", "folder": "GBA", "detect": "mGBA.exe" }""");
        Assert.Equal(1, Recipes.Import(f.Db, local));
        var preview = await new GitHubPublisher(f.Db, "secreto", github) { Work = f.Tmp["trabajo-recetas"] }.Preview(new("all", 0));
        Assert.Equal(["recetas/mgba.json", "sagas/00033-final-fantasy.json"], preview.Files.Select(x => x.Path));
    }

    /// <summary>#96: las recetas de Otteretro se ven como locales y Publicar las ofrece; sin tocar esa carpeta ni mirar `_respaldos`.</summary>
    [Fact]
    public async Task TheRecipesOfOtteretroAreLocalAndPublishedOnce()
    {
        var (f, _, _) = Setup(canPush: true);
        await using var _f = f;
        var data = f.Tmp["datos-otteretro"];
        var folder = Directory.CreateDirectory(Path.Combine(data, "data", "recetas")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "_respaldos"));
        var text = """{ "id": "mgba", "name": "mGBA", "kind": "emulator", "folder": "GBA", "detect": "mGBA.exe" }""";
        File.WriteAllText(Path.Combine(folder, "mgba.json"), text);
        File.WriteAllText(Path.Combine(folder, "_respaldos", "viejo.json"), text.Replace("mgba", "viejo"));
        {
            Assert.Equal([("mgba", true)], Recipes.List(f.Db, folder).Select(r => (r.Id, r.Local)));
            var github = new FakeGitHub(MainZip(f, _ => { }), canPush: true);
            var preview = await new GitHubPublisher(f.Db, "secreto", github) { Work = f.Tmp["trabajo-locales"], LocalRecipes = folder }.Preview(new("all", 0));
            Assert.Equal(["recetas/mgba.json"], preview.Files.Select(x => x.Path));
            Assert.Equal(text, File.ReadAllText(Path.Combine(folder, "mgba.json"))); // solo lectura

            // Ya en main: no se ofrece otra vez.
            var published = new FakeGitHub(MainZip(f, m =>
            {
                Directory.CreateDirectory(Path.Combine(m, Published.RecipesFolder));
                File.WriteAllText(Path.Combine(m, Published.RecipesFolder, "mgba.json"), text);
            }), canPush: true);
            Assert.Empty((await new GitHubPublisher(f.Db, "secreto", published) { Work = f.Tmp["trabajo-publicadas"], LocalRecipes = folder }.Preview(new("all", 0))).Files);

            // Con la receta en la base (traída de main), deja de ser local.
            Assert.Equal(1, Recipes.Import(f.Db, folder));
            Assert.Equal([("mgba", false)], Recipes.List(f.Db, folder).Select(r => (r.Id, r.Local)));
        }
    }

    /// <summary>#85: volver a la vista previa o publicar reusa la comparación; un cambio local la rehace.</summary>
    [Fact]
    public async Task ThePlanIsReusedUntilSomethingChanges()
    {
        var (f, _, publisher) = Setup(canPush: true, f => Assert.Null(Editing.SetIconic(f.Db, 33, true)));
        await using var _ = f;
        var mark = Path.Combine(publisher.Work, "local", "marca");

        Assert.Single((await publisher.Preview(new("saga", 33))).Files);
        File.WriteAllText(mark, ""); // armar el plan otra vez borra la carpeta local
        Assert.Single((await publisher.Preview(new("saga", 33))).Files);
        await publisher.Publish(new("saga", 33));
        Assert.True(File.Exists(mark));

        Assert.Null(Editing.SetIconic(f.Db, 40, true));
        Assert.Single((await publisher.Preview(new("saga", 33))).Files);
        Assert.False(File.Exists(mark));
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

    /// <summary>#85: lo aceptado en Revisar (rutas, icónico, mejor versión, traducción) y un port asignado a mano salen en lo publicado.</summary>
    [Fact]
    public async Task WhatWasAcceptedInReviewIsPublished()
    {
        var (f, _, publisher) = Setup(canPush: true, f =>
        {
            using var c = f.Db.Open();
            long Item(string prompt, string entity, long id, object result) => c.ExecuteScalar<long>("""
                INSERT INTO review_item (kind, entity, entity_id, data, created_at) VALUES ('pending_approval', @entity, @id, @data, '2026-11-08') RETURNING id
                """, new { entity, id, data = JsonSerializer.Serialize(new { prompt, result }, JsonSerializerOptions.Web) });
            var good = new A.Criterion(4, "Bien");
            foreach (var id in new[]
            {
                Item(A.Routes, "saga", 33, new List<A.Route> { new("release", "Por fecha", [new(10, 20, "El primero")], []) }),
                Item(A.Iconic, "saga", 33, new A.SagaIconic(33, true, [10], "Cambió el género", [])),
                Item(A.BestVersion, "game", 10, new A.GameBest(10, 20, "La única", [new(20, good, good, good, good)])),
                Item(A.Translate, "game", 10, new { text = "Tidus despierta en Zanarkand." }),
            }) Assert.True(ClaudeReview.Approve(f.Db, id));
            c.Execute("""
                INSERT INTO catalog.port (id, console, game, project, kind, status, link, source, added) VALUES
                  (1, 'gb', 'Tetris', 'tetris-pc', 'port', 'complete', 'https://example.org/tetris', 'extra', '2026-11-08')
                """);
            Assert.True(Ports.Assign(f.Db, 1, 12));
        });
        await using var _ = f;

        var preview = await publisher.Preview(new("all", 0));
        Assert.Empty(preview.Errors);
        Assert.Equal(["sagas/00033-final-fantasy.json", "sin-saga/00000.json"], preview.Files.Select(x => x.Path));
        var saga = JsonNode.Parse(File.ReadAllText(Path.Combine(publisher.Work, "local", "sagas", "00033-final-fantasy.json")))!;
        var game = saga["games"]![0]!;
        Assert.True(saga["iconic"]!.GetValue<bool>() && game["iconic"]!.GetValue<bool>());
        Assert.Equal("El primero", saga["routes"]![0]!["steps"]![0]!["reason"]!.GetValue<string>());
        var fields = game["fields"]!.AsArray().ToDictionary(x => x!["field"]!.GetValue<string>(), x => x!["value"]?.GetValue<string>());
        Assert.Equal(("20", "Tidus despierta en Zanarkand."), (fields["best_version"], fields["summary_es"]));
        Assert.Equal(4, game["versions"]![0]!["criteria"]!.AsArray().Count);
        var tetris = JsonNode.Parse(File.ReadAllText(Path.Combine(publisher.Work, "local", "sin-saga", "00000.json")))!["games"]![0]!;
        Assert.Equal("tetris-pc", tetris["ports"]![0]!["project"]!.GetValue<string>());
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

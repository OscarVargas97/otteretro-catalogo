using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Otteretro.Catalog;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>#97 sin red: la tarea «Emuladores» revisa cada receta y busca forks; lo seguro se aplica, lo que rompe y los forks van a Revisar.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch, ClaudeBudget.UtcNow, Ports.Handler y Recipes.Http
public class EmulatorCheckTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<string> claude = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> realLaunch = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;
    readonly HttpClient realHttp = Recipes.Http;

    static string Ago(int days) => DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
    static string Repo(string name, int stars, bool fork = false, bool archived = false, int created = 400, int pushed = 5) =>
        $$"""{ "full_name": "{{name}}", "stargazers_count": {{stars}}, "fork": {{(fork ? "true" : "false")}}, "archived": {{(archived ? "true" : "false")}}, "created_at": "{{Ago(created)}}", "pushed_at": "{{Ago(pushed)}}", "description": "" }""";
    static string Release(string tag, int days, params string[] files) =>
        $$"""[{ "tag_name": "{{tag}}", "draft": false, "published_at": "{{Ago(days)}}", "assets": [{{string.Join(",", files.Select(a => $$"""{ "name": "{{a}}", "browser_download_url": "https://dl/{{a}}" }"""))}}] }]""";

    /// <summary>La red: cada URL pedida con su respuesta; lo que no está es un 404.</summary>
    sealed class Net : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Bodies = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            if (url.StartsWith("https://dl/", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            var body = Bodies.FirstOrDefault(b => url.Contains(b.Key, StringComparison.Ordinal)).Value;
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    readonly Net net = new();

    public EmulatorCheckTests()
    {
        Recipes.Http = new HttpClient(net);
        Ports.Handler = net;
        EmulatorCheck.SearchDelay = TimeSpan.Zero;
        ClaudeBudget.UtcNow = () => TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(3), TimeZoneInfo.Local); // de noche
        const string gh = "https://api.github.com/repos/";
        var b = net.Bodies;
        // mgba: release nueva que calza → suma su descarga sola.
        b[gh + "mgba-emu/mgba/releases"] = Release("0.11", 3, "mGBA-0.11-macos.dmg", "mGBA-0.11-win64.7z");
        b[gh + "mgba-emu/mgba/forks"] = "[" + string.Join(",",
            Repo("a/mgba-good", 50, fork: true), Repo("b/mgba-few", 3, fork: true), Repo("c/mgba-same", 20, fork: true, created: 30, pushed: 30),
            Repo("d/mgba-old", 20, fork: true)) + "]";
        b[gh + "a/mgba-good/releases"] = Release("v2", 10, "mgba-good-windows-x64.zip");
        b[gh + "d/mgba-old/releases"] = Release("v1", 400, "mgba-old-win64.zip");
        b["search/repositories?q=mGBA in:name"] = """{ "items": [""" + Repo("e/mgba-plus", 100) + "] }";
        b[gh + "e/mgba-plus/releases"] = Release("1.0", 20, "mgba-plus-win64.zip");
        b[gh + "mgba-emu/mgba"] = Repo("mgba-emu/mgba", 5000);
        // Lo que rompe: movido, archivado y un patrón que ya no calza (dolphin no existe: 404).
        b[gh + "cemu-project/Cemu"] = Repo("other/Cemu", 100);
        b[gh + "hrydgard/ppsspp"] = Repo("hrydgard/ppsspp", 100, archived: true);
        b[gh + "PCSX2/pcsx2/releases"] = Release("v3.0", 5, "pcsx2-v3.0-windows-x64-Qt.7z");
        b[gh + "PCSX2/pcsx2"] = Repo("PCSX2/pcsx2", 100);
        // Una lista conocida trae un emulador que pasa los filtros pero no es un fork.
        b["alnacle/awesome-emulators"] = "- [Other](https://github.com/f/other-emu) - Un emulador distinto.";
        b[gh + "f/other-emu/releases"] = Release("2.0", 30, "other-emu-win64.zip");
        b[gh + "f/other-emu"] = Repo("f/other-emu", 30);

        using var c = f.Db.Open();
        void Add(string id, string kind, string repo, string asset, string extra = "") => c.Execute("INSERT INTO recipe (id, kind, json) VALUES (@id, @kind, @json)", new
        {
            id, kind,
            json = $$"""{ "id": "{{id}}", "name": "{{(id == "mgba" ? "mGBA" : id)}}", "kind": "{{kind}}", "folder": "Game Boy Advance", "detect": "mGBA.exe", "consoles": ["gba"], "updates": { "source": "github", "repo": "{{repo}}" }, "asset": "{{asset}}"{{extra}} }""",
        });
        Add("mgba", "emulator", "mgba-emu/mgba", @"^mGBA-[0-9.]+-win64\\.7z$",
            """, "downloads": [{ "version": "0.10.5", "system": "windows", "url": "https://dl/mGBA-0.10.5-win64.7z", "hash": "sha256-x" }]""");
        Add("dolphin", "tool", "dolphin-emu/dolphin", "x");
        Add("cemu", "tool", "cemu-project/Cemu", "x");
        Add("ppsspp", "tool", "hrydgard/ppsspp", "x");
        Add("pcsx2", "tool", "PCSX2/pcsx2", @"^pcsx2-v[0-9.]+-windows-x64\\.7z$");

        ClaudeRunner.Launch = (start, stdin, ct) =>
        {
            claude.Add(stdin);
            Assert.Contains(EmulatorCheck.Model, start.ArgumentList);
            var reply = new EmulatorCheck.Reply([
                new("https://github.com/e/mgba-plus", true, "mgba", [new("Pokémon Emerald", "https://example.org/foro")], "Sale de mGBA y suma netplay."),
                new("https://github.com/f/other-emu", false, "", [], "Es otro emulador, con su propio código."),
            ]);
            return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new
            {
                is_error = false, structured_output = reply, total_cost_usd = 0.01,
                usage = new { input_tokens = 100, output_tokens = 10, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            }, JsonSerializerOptions.Web), ""));
        };
    }

    public async ValueTask DisposeAsync()
    {
        Recipes.Http = realHttp;
        Ports.Handler = null;
        ClaudeRunner.Launch = realLaunch;
        ClaudeBudget.UtcNow = realNow;
        EmulatorCheck.SearchDelay = TimeSpan.FromSeconds(2.5);
        await f.DisposeAsync();
    }

    async Task Done()
    {
        Assert.True(CatalogTasks.Run(f.Db, "emulators"));
        long id;
        using (var c = f.Db.Open()) id = c.ExecuteScalar<long>("SELECT max(id) FROM task WHERE kind = @Kind", new { EmulatorCheck.Kind });
        var task = await f.Run(id);
        Assert.True(task.Status == "done", $"{task.Status}: {JsonSerializer.Serialize(task.Detail)}");
    }

    [Fact]
    public async Task RecipesAreCheckedAndOnlyNewForksThatPassTheFiltersGoToReview()
    {
        await Done();
        using (var c = f.Db.Open())
        {
            // La release nueva se aplicó sola, con su hash, sin pasar por Revisar.
            var mgba = c.ExecuteScalar<string>("SELECT json FROM recipe WHERE id = 'mgba'");
            Assert.Contains("\"version\": \"0.11\"", mgba);
            Assert.Contains("https://dl/mGBA-0.11-win64.7z", mgba);
            Assert.Contains("sha256-", mgba);

            var reasons = c.Query<(string Recipe, string Reason)>(
                "SELECT json_extract(data, '$.recipe'), json_extract(data, '$.reason') FROM review_item WHERE entity = 'recipe' ORDER BY 1").ToList();
            Assert.Equal([("cemu", "moved"), ("dolphin", "gone"), ("pcsx2", "noAsset"), ("ppsspp", "archived")], reasons);
            // Con un solo archivo de Windows en la release, propone el patrón que sale de él.
            Assert.Contains("[0-9.]+", c.ExecuteScalar<string>("SELECT json_extract(data, '$.proposed') FROM review_item WHERE data LIKE '%pcsx2%'"));

            // Forks: el de su repo entra sin Claude; el buscado por nombre, con lo que dijo Sonnet; los que no pasan los filtros, no.
            var forks = c.Query<string>("SELECT data FROM review_item WHERE entity = 'fork' ORDER BY id").Select(d => JsonDocument.Parse(d).RootElement).ToList();
            Assert.Equal(["a/mgba-good", "e/mgba-plus"], forks.Select(x => x.GetProperty("repo").GetString()));
            Assert.All(forks, x => Assert.Equal("mgba", x.GetProperty("recipe").GetString()));
            Assert.Equal(50, forks[0].GetProperty("passed").GetProperty("stars").GetInt32());
            Assert.Equal("Pokémon Emerald", forks[1].GetProperty("recommendedFor")[0].GetProperty("game").GetString());
            Assert.Equal("https://example.org/foro", forks[1].GetProperty("recommendedFor")[0].GetProperty("source").GetString());

            Assert.Equal(["github.com/a/mgba-good:review", "github.com/b/mgba-few:filtered", "github.com/c/mgba-same:filtered", "github.com/d/mgba-old:filtered",
                "github.com/e/mgba-plus:review", "github.com/f/other-emu:rejected"], c.Query<string>("SELECT repo || ':' || result FROM fork_seen ORDER BY repo"));
        }
        var input = Assert.Single(claude);
        Assert.Contains("e/mgba-plus", input);
        Assert.Contains("f/other-emu", input);
        Assert.DoesNotContain("a/mgba-good", input); // seguro: salió de los forks de la receta
        Assert.Equal(6, CatalogTasks.PendingCounts(f.Db)["emulators"]);
        Assert.Contains(CatalogTasks.List(f.Db), t => t.Id == "emulators" && t.Model == "sonnet");

        // Otra vez: no repite candidatos, ni problemas, ni vuelve a consultar a Claude.
        await Done();
        Assert.Single(claude);
        Assert.Equal(6, CatalogTasks.PendingCounts(f.Db)["emulators"]);
        using (var c = f.Db.Open())
            Assert.Equal(2, c.ExecuteScalar<int>("SELECT json_array_length(json, '$.downloads') FROM recipe WHERE id = 'mgba'"));
    }
    [Fact]
    public async Task AcceptingAForkCreatesItsExperimentalRecipeAndRejectingMarksItSeen()
    {
        await Done();
        var page = ClaudeReview.Page(f.Db);
        Assert.Equal(["a/mgba-good", "e/mgba-plus"], page.Forks.Select(x => x.Repo));
        Assert.Equal(["cemu", "dolphin", "pcsx2", "ppsspp"], page.Notices.Select(x => x.Recipe));

        var (found, error) = await EmulatorReview.Accept(f.Db, page.Forks[1].Id, null);
        Assert.True(found);
        Assert.Null(error);
        using (var doc = JsonDocument.Parse(Recipes.Get(f.Db, "e-mgba-plus")!))
        {
            var r = doc.RootElement;
            Assert.True(r.GetProperty("experimental").GetBoolean());
            Assert.Equal("mgba", r.GetProperty("forkOf").GetString());
            Assert.Equal("e/mgba-plus", r.GetProperty("updates").GetProperty("repo").GetString());
            Assert.Equal("Pokémon Emerald", r.GetProperty("recommendedFor")[0].GetProperty("game").GetString());
            Assert.False(r.TryGetProperty("downloads", out _));
        }
        Assert.False((await EmulatorReview.Accept(f.Db, page.Forks[1].Id, null)).Found); // ya resuelto

        Assert.True(EmulatorReview.Dismiss(f.Db, page.Forks[0].Id));
        Assert.True(EmulatorReview.Dismiss(f.Db, page.Notices[0].Id));
        page = ClaudeReview.Page(f.Db);
        Assert.Empty(page.Forks);
        Assert.Equal(3, page.Notices.Count);
        using var c = f.Db.Open();
        Assert.Equal("rejected", c.ExecuteScalar<string>("SELECT result FROM fork_seen WHERE repo = 'github.com/a/mgba-good'"));
    }
}

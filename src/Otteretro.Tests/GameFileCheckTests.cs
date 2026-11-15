using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Otteretro.Catalog;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>#198 sin red: «Archivo del juego» junta la evidencia, Haiku decide, lo seguro se guarda, lo dudoso va a la tarjeta y lo visto no se repite.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch, ClaudeBudget.UtcNow y Recipes.Http
public class GameFileCheckTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<string> claude = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> realLaunch = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;
    readonly HttpClient realHttp = Recipes.Http;

    sealed class Net : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Bodies = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = Bodies.GetValueOrDefault(Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri));
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    public GameFileCheckTests()
    {
        var net = new Net();
        Recipes.Http = new HttpClient(net);
        ClaudeBudget.UtcNow = () => TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(3), TimeZoneInfo.Local); // de noche
        using var c = f.Db.Open();
        foreach (var id in new[] { "sure", "doubt", "unknown", "bad", "done" })
        {
            var extra = id == "done" ? """, "gameFile": { "deliver": "none" }""" : "";
            c.Execute("INSERT INTO recipe (id, kind, json) VALUES (@id, 'port', @json)", new
            {
                id,
                json = $$"""{ "id": "{{id}}", "name": "{{id}}", "kind": "port", "folder": "x", "detect": "{{id}}.exe", "consoles": ["n64"], "updates": { "source": "github", "repo": "o/{{id}}" }, "asset": "^{{id}}\\.zip$"{{extra}} }""",
            });
            var readme = Convert.ToBase64String(Encoding.UTF8.GetBytes($"# {id}\nRun it with --rom-file path/to/baserom.z64\n"));
            net.Bodies[$"https://api.github.com/repos/o/{id}/readme"] = $$"""{ "content": "{{readme}}" }""";
            net.Bodies[$"https://api.github.com/repos/o/{id}/git/trees/HEAD?recursive=1"] = """{ "tree": [{ "path": "src/main.cpp" }, { "path": "assets/logo.png" }, { "path": "x/y.o" }] }""";
        }

        ClaudeRunner.Launch = (start, stdin, ct) =>
        {
            claude.Add(stdin);
            Assert.Contains(GameFileCheck.Model, start.ArgumentList);
            var reply = new GameFileCheck.Reply([
                new("sure", "arg", ["--rom-file", "{rom}"], "", [".Z64", "n64"], true, "El README da el argumento."),
                new("doubt", "copy", [], "baserom.us.z64", ["z64"], false, "Parece buscarlo junto al exe."),
                new("unknown", "unknown", [], "", [], false, "El README no dice nada."),
                new("bad", "copy", [], "../fuera.z64", [], true, "Sale de la instalación."),
            ]);
            return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new
            {
                is_error = false,
                structured_output = reply,
                total_cost_usd = 0.001,
                usage = new { input_tokens = 100, output_tokens = 10, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            }, JsonSerializerOptions.Web), ""));
        };
    }

    public async ValueTask DisposeAsync()
    {
        Recipes.Http = realHttp;
        ClaudeRunner.Launch = realLaunch;
        ClaudeBudget.UtcNow = realNow;
        await f.DisposeAsync();
    }

    async Task Done()
    {
        Assert.True(CatalogTasks.Run(f.Db, "gameFile"));
        long id;
        using (var c = f.Db.Open()) id = c.ExecuteScalar<long>("SELECT max(id) FROM task WHERE kind = @Kind", new { GameFileCheck.Kind });
        var task = await f.Run(id);
        Assert.True(task.Status == "done", $"{task.Status}: {JsonSerializer.Serialize(task.Detail)}");
    }

    [Fact]
    public async Task OnlyPortsWithoutGameFileAreAskedOnceAndOnlyWhatIsSureIsSaved()
    {
        Assert.Equal(4, GameFileCheck.Pending(f.Db));
        await Done();

        var input = Assert.Single(claude);
        Assert.Contains("--rom-file path/to/baserom.z64", input); // la evidencia del README va en la entrada
        Assert.Contains("src/main.cpp", input);
        Assert.DoesNotContain("x/y.o", input);
        Assert.DoesNotContain("o/done", input); // ya tiene gameFile

        using (var c = f.Db.Open())
        {
            var sure = JsonDocument.Parse(c.ExecuteScalar<string>("SELECT json FROM recipe WHERE id = 'sure'")!).RootElement.GetProperty("gameFile");
            Assert.Equal("arg", sure.GetProperty("deliver").GetString());
            Assert.Equal(["--rom-file", "{rom}"], sure.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(["z64", "n64"], sure.GetProperty("formats").EnumerateArray().Select(x => x.GetString()));
            foreach (var id in new[] { "doubt", "unknown", "bad" })
                Assert.DoesNotContain("gameFile", c.ExecuteScalar<string>("SELECT json FROM recipe WHERE id = @id", new { id }));
            var notice = Assert.Single(c.Query<string>("SELECT data FROM review_item WHERE entity = 'recipe' AND status = 'open'"));
            Assert.Contains("\"recipe\":\"doubt\"", notice);
            Assert.Contains("\"reason\":\"gameFile\"", notice);
        }

        // Lo visto no se vuelve a pedir: la segunda vez no llama a Claude.
        Assert.Equal(0, GameFileCheck.Pending(f.Db));
        await Done();
        Assert.Single(claude);

        // Si cambia el asset, vuelve a estar pendiente.
        using (var c = f.Db.Open()) c.Execute("UPDATE recipe SET json = json_set(json, '$.asset', 'otro') WHERE id = 'unknown'");
        Assert.Equal(1, GameFileCheck.Pending(f.Db));
    }

    [Fact]
    public void ALongReadmeKeepsTheLinesAboutTheGame()
    {
        var readme = string.Join('\n', Enumerable.Range(0, 2000).Select(i => i == 1500 ? "Place baserom.us.z64 next to the exe" : $"línea de relleno {i}"));
        var excerpt = GameFileCheck.Excerpt(readme);
        Assert.Contains("Place baserom.us.z64 next to the exe", excerpt);
        Assert.True(excerpt.Length < 1000);
    }
}

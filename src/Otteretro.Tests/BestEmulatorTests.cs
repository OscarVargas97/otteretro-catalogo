using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Otteretro.Catalog;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>#130 sin red: «Mejor emulador» de un juego deja cada recomendación en Revisar y no repite lo ya visto.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch y ClaudeBudget.UtcNow
public class BestEmulatorTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<string> claude = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> realLaunch = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;

    public BestEmulatorTests()
    {
        ClaudeBudget.UtcNow = () => TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(3), TimeZoneInfo.Local); // de noche
        using (var c = f.Db.Open())
        {
            c.Execute("""
                INSERT INTO game (id, name_es, original_name, year) VALUES (7, 'Pokémon Esmeralda', 'Pokémon Emerald', 2004), (8, 'Otro', NULL, NULL);
                INSERT INTO version (game_id, platform) VALUES (7, 'gba'), (7, 'ds');
                """);
            void Add(string id, string console) => c.Execute("INSERT INTO recipe (id, kind, json) VALUES (@id, 'emulator', @json)", new
            {
                id, json = $$"""{ "id": "{{id}}", "name": "{{id}}", "kind": "emulator", "consoles": ["{{console}}"], "updates": { "source": "github", "repo": "x/{{id}}" } }""",
            });
            Add("mgba", "gba");
            Add("pcsx2", "ps2");
        }
        ClaudeRunner.Launch = (start, stdin, ct) =>
        {
            claude.Add(stdin);
            Assert.Contains(BestEmulator.Model, start.ArgumentList);
            var reply = new BestEmulator.Reply([
                new("gba", "mgba", "", "https://example.org/gba", "El que mejor corre el juego."),
                new("ds", "", "https://github.com/y/melonds-fork/", "https://example.org/ds", "Un fork que arregla el enlace."),
                new("ps2", "pcsx2", "", "https://example.org/ps2", "El juego no existe en PS2."), // otra consola
                new("ds", "mgba", "", "https://example.org/x", "mGBA no corre DS."), // receta de otra consola
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
        ClaudeRunner.Launch = realLaunch;
        ClaudeBudget.UtcNow = realNow;
        await f.DisposeAsync();
    }

    async Task Search(long gameId)
    {
        var id = BestEmulator.Enqueue(f.Db, gameId)!.Value;
        Assert.Equal(id, BestEmulator.Enqueue(f.Db, gameId)); // ya en la cola: la misma
        var task = await f.Run(id);
        Assert.True(task.Status == "done", $"{task.Status}: {JsonSerializer.Serialize(task.Detail)}");
    }

    List<JsonElement> Picks(string status = "open")
    {
        using var c = f.Db.Open();
        return c.Query<string>("SELECT data FROM review_item WHERE entity = 'emulatorPick' AND status = @status ORDER BY id", new { status })
            .Select(d => JsonDocument.Parse(d).RootElement).ToList();
    }

    [Fact]
    public async Task EachPickGoesToReviewOnceAndTheSearchDateStays()
    {
        Assert.Null(BestEmulator.Enqueue(f.Db, 999)); // no existe
        Assert.Null(BestEmulator.LastSearch(f.Db, 7));
        await Search(7);

        var input = JsonDocument.Parse(Assert.Single(claude)).RootElement;
        Assert.Equal(["ds", "gba"], input.GetProperty("consoles").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["mgba"], input.GetProperty("recipes").EnumerateArray().Select(x => x.GetProperty("id").GetString())); // solo las de sus consolas

        var picks = Picks();
        Assert.Equal(2, picks.Count);
        Assert.Equal(7, picks[0].GetProperty("gameId").GetInt64());
        Assert.Equal("Pokémon Esmeralda", picks[0].GetProperty("game").GetString());
        Assert.Equal(("gba", "mgba"), (picks[0].GetProperty("console").GetString(), picks[0].GetProperty("recipe").GetString()));
        Assert.Equal("https://example.org/gba", picks[0].GetProperty("source").GetString());
        Assert.Equal("El que mejor corre el juego.", picks[0].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, picks[1].GetProperty("recipe").ValueKind);
        Assert.Equal("https://github.com/y/melonds-fork", picks[1].GetProperty("repo").GetString());
        var first = BestEmulator.LastSearch(f.Db, 7);
        Assert.NotNull(first);

        // Aceptado o descartado (o todavía abierto): la misma recomendación no vuelve a entrar.
        using (var c = f.Db.Open()) c.Execute("UPDATE review_item SET status = CASE WHEN data LIKE '%mgba%' THEN 'approved' ELSE 'rejected' END WHERE entity = 'emulatorPick'");
        await Search(7);
        Assert.Equal(2, claude.Count);
        Assert.Empty(Picks());
        Assert.Single(Picks("approved"));
        Assert.Single(Picks("rejected"));
        Assert.True(string.CompareOrdinal(BestEmulator.LastSearch(f.Db, 7), first) >= 0);
    }

    [Fact]
    public async Task AGameWithoutConsolesDoesNotCallClaude()
    {
        await Search(8);
        Assert.Empty(claude);
        Assert.Empty(Picks());
        Assert.NotNull(BestEmulator.LastSearch(f.Db, 8));
    }

    [Fact]
    public async Task OnlyTheChosenGamesAreListedAndQueued()
    {
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO saga (id, name) VALUES (1, 'Pokémon'), (2, 'Otra');
                UPDATE game SET saga_id = 1 WHERE id = 7;
                INSERT INTO game (id, saga_id, name_es) VALUES (9, 1, 'Pokémon Rubí'), (10, 2, 'Fuera');
                """);
        await Search(7);

        // Las sagas elegidas y los juegos sueltos; lo de otras sagas no. El ya buscado trae su fecha.
        var choices = BestEmulator.Choices(f.Db, [1], [8]);
        Assert.Equal([7L, 9, 8], choices.Select(g => g.Id));
        Assert.Equal(["ds", "gba"], choices[0].Platforms.Order());
        Assert.Equal("Pokémon", choices[0].Saga);
        Assert.NotNull(choices[0].SearchedAt);
        Assert.Null(choices[1].SearchedAt);
        Assert.Empty(BestEmulator.Choices(f.Db, [], []));

        // Solo se encolan los marcados; los que no existen se saltan.
        Assert.Equal(2, BestEmulator.Enqueue(f.Db, [9, 8, 9, 999]));
        using var db = f.Db.Open();
        Assert.Equal(["8", "9"], db.Query<string>("SELECT data FROM task WHERE kind = @Kind AND status = 'queued' ORDER BY data", new { BestEmulator.Kind }));
    }
}

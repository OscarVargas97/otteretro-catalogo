using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>§11.5, F2.21: lo que Claude repite 10 veces se sugiere automatizar y «Crear issue» lo deja en GitHub una sola vez.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparte ClaudeRunner.Launch
public class AutomationTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> real = ClaudeRunner.Launch;

    public async ValueTask DisposeAsync()
    {
        ClaudeRunner.Launch = real;
        await f.DisposeAsync();
    }

    void Learn(int times, string reason)
    {
        using var c = f.Db.Open();
        for (var i = 0; i < times; i++)
            c.Execute("INSERT INTO learning (kind, content, created_at) VALUES ('rule', @content, '2026-10-31')",
                new { content = JsonSerializer.Serialize(new { change = new { kind = "merge", reason = reason + (i % 2 == 0 ? "." : $" {i}") } }) });
    }

    [Fact]
    public async Task RepeatedChangesAreSuggestedAndTheIssueIsCreatedOnce()
    {
        Learn(10, "El 3D es un remake del mismo juego");
        Learn(9, "Es la misma ficha");
        var suggestion = Assert.Single(Automation.Suggestions(f.Db));
        Assert.Equal(("merge", 10), (suggestion.Change, suggestion.Count));

        var calls = new List<(List<string> Args, string Body)>();
        ClaudeRunner.Launch = (start, stdin, ct) =>
        {
            calls.Add(([.. start.ArgumentList], stdin));
            return Task.FromResult(new ProcessOutput(0, "https://github.com/OscarVargas97/otteretro/issues/999\n", ""));
        };
        Assert.Null(await Automation.CreateIssue(f.Db, suggestion.Key, CancellationToken.None));
        Assert.Null(await Automation.CreateIssue(f.Db, suggestion.Key, CancellationToken.None)); // ya existe: no crea otro

        var (args, body) = Assert.Single(calls);
        Assert.Contains("automatizar", args);
        Assert.Contains("10 veces", body);
        Assert.Equal("https://github.com/OscarVargas97/otteretro/issues/999", Automation.Suggestions(f.Db)[0].IssueUrl);

        ClaudeRunner.Launch = (_, _, _) => throw new System.ComponentModel.Win32Exception();
        Learn(10, "Otra razón repetida");
        var other = Automation.Suggestions(f.Db).Single(s => s.IssueUrl is null);
        Assert.Equal("automation.ghMissing", (await Automation.CreateIssue(f.Db, other.Key, CancellationToken.None))?.Code);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <param name="Key">Identifica la sugerencia (tipo de cambio y razón normalizada).</param>
/// <param name="Examples">Hasta 5 razones tal como las escribió Claude.</param>
/// <param name="IssueUrl">El issue que ya se creó para esta sugerencia.</param>
public record AutomationSuggestion(string Key, string Change, string Reason, int Count, List<string> Examples, string? IssueUrl);

/// <summary>
/// La app evoluciona (§11.5, F2.21): cuando Claude aplicó muchas veces el mismo tipo de cambio por la misma razón, conviene un
/// script que lo haga sin gastar tokens. Ajustes › Claude lo sugiere y «Crear issue» lo deja en GitHub con `gh` y la
/// etiqueta `automatizar`; después se implementa con Claude Code en el repositorio.
/// </summary>
public static class Automation
{
    public const int Threshold = 10;
    public const string Repo = "OscarVargas97/otteretro";
    const string Created = "automation.issues"; // JSON { key: url } de lo ya creado

    public static string Gh { get; set; } = "gh";

    // ponytail: se agrupa por la razón normalizada (minúsculas, sin números ni signos); razones parecidas con otras palabras
    // no se juntan. Si se quedan cortas, agrupar por patrones o pedirle a Claude que las clasifique.
    static string Normalize(string? reason) =>
        Regex.Replace(Regex.Replace((reason ?? "").ToLowerInvariant(), @"[^\p{L}\s]", " "), @"\s+", " ").Trim();

    static string KeyOf(string change, string reason) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{change}|{reason}")))[..12].ToLowerInvariant();

    public static List<AutomationSuggestion> Suggestions(Database db)
    {
        using var c = db.Open();
        var created = CreatedIssues(c);
        return c.Query<string>("SELECT content FROM learning WHERE kind = 'rule' AND undone = 0").Select(content =>
            {
                using var doc = JsonDocument.Parse(content);
                var change = doc.RootElement.TryGetProperty("change", out var ch) ? ch : doc.RootElement;
                return (Change: change.TryGetProperty("kind", out var k) ? k.GetString() ?? "?" : "?",
                    Reason: change.TryGetProperty("reason", out var r) ? r.GetString() : null);
            })
            .GroupBy(l => (l.Change, Reason: Normalize(l.Reason)))
            .Where(g => g.Key.Reason != "" && g.Count() >= Threshold)
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var key = KeyOf(g.Key.Change, g.Key.Reason);
                return new AutomationSuggestion(key, g.Key.Change, g.First().Reason!, g.Count(),
                    g.Select(l => l.Reason!).Distinct().Take(5).ToList(), created.GetValueOrDefault(key));
            })
            .ToList();
    }

    static Dictionary<string, string> CreatedIssues(System.Data.IDbConnection c) =>
        c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @Created", new { Created }) is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];

    /// <summary>Crea el issue de una sugerencia con `gh`. Devuelve el error o null; si ya existía, no crea otro.</summary>
    public static async Task<ApiError?> CreateIssue(Database db, string key, CancellationToken ct)
    {
        var suggestion = Suggestions(db).FirstOrDefault(s => s.Key == key);
        if (suggestion is null) return ApiError.Of("automation.notFound");
        if (suggestion.IssueUrl is not null) return null;

        var title = $"Automatizar: {suggestion.Change} · {suggestion.Reason}";
        var body = $"""
            Sale de: Ajustes › Claude › Sugerencias de automatizar (§11.5).

            Claude aplicó **{suggestion.Count} veces** un cambio de tipo `{suggestion.Change}` por la misma razón. Un script que lo
            haga sin Claude ahorra tokens («Claude decide y los scripts repiten»).

            ## Ejemplos de la razón
            {string.Join("\n", suggestion.Examples.Select(e => $"- {e}"))}

            ## Propuesta
            Revisar los aprendizajes de ese tipo en Revisar › Aprendizajes y convertir la regla en código en la depuración del catálogo.
            """;
        var start = new ProcessStartInfo(Gh) { CreateNoWindow = true };
        foreach (var arg in (string[])["issue", "create", "-R", Repo, "--label", "automatizar", "--title", title, "--body-file", "-"])
            start.ArgumentList.Add(arg);
        ProcessOutput output;
        try { output = await ClaudeRunner.Launch(start, body, ct); } // el mismo lanzador de procesos que `claude`
        catch (Win32Exception) { return ApiError.Of("automation.ghMissing"); }
        if (output.ExitCode != 0) return ApiError.Of("automation.ghFailed", ("detail", output.Stderr.Trim()));

        using var c = db.Open();
        var created = CreatedIssues(c);
        created[key] = output.Stdout.Trim();
        ClaudeBudget.Set(c, Created, JsonSerializer.Serialize(created));
        return null;
    }
}

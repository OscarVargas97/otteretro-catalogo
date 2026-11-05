using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Buscar ports nuevos (#71, reemplaza a `buscar-nuevos.cmd`): script primero. El RSS de Recompendium trae consola, estado y repositorio
/// de cada recomp, y eso entra sin Claude. Lo que el script no puede saber (los ports del RSS sin consola y los enlaces de las listas
/// conocidas, como Read Only Memo) va a Claude, que lo verifica en su fuente primaria. La huella es el enlace: lo verificado, entre o
/// no, no se vuelve a pedir. Solo agrega: nunca borra ni cambia un port de la lista.
/// </summary>
public static partial class PortSearch
{
    public const string Kind = "ports.search", PromptName = "verificar-port", Model = "sonnet";
    const string Rss = "https://recompendium.com/en/rss.xml";
    const string ReadOnlyMemo = "https://readonlymemo.com/decompilation-projects-and-n64-recompiled-list/";

    /// <summary>Los que van a Claude en una ejecución (como `prompt-buscar-nuevos.md`) y por llamada.</summary>
    public static int MaxCandidates { get; set; } = 25;
    const int Batch = 5;

    public static long Enqueue(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = @Kind AND status IN ('queued', 'running') LIMIT 1", new { Kind }) ?? TaskQueue.Enqueue(db, Kind);
    }

    /// <param name="Hint">La sección o la descripción donde apareció.</param>
    public record Candidate(string Link, string Name, string Hint, string Source);

    public record Verdict(string Link, bool Verified, string Console, string Game, string Project, string Kind, string? Toolchain, string Status, string Reason);
    public record Reply(List<Verdict> Ports);

    static readonly string[] Kinds = ["decomp", "recomp", "remake", "port", "native-layer", "engine"];
    static readonly string[] Statuses = ["complete", "playable", "beta", "experimental"];

    public static async Task Run(TaskRun run, CancellationToken ct)
    {
        var db = run.Db;
        using var http = Ports.Http();
        run.Report(0, ApiError.Of("ports.search.reading"));
        var (sure, doubtful) = (new List<Ports.Entry>(), new List<Candidate>());
        try { (sure, doubtful) = ReadRss(await http.GetStringAsync(Rss, ct)); }
        catch (Exception e) when (e is HttpRequestException or System.Xml.XmlException) { run.Log(ApiError.Of("ports.log.unreachable", ("source", "Recompendium RSS")), "warn"); }
        try { doubtful.AddRange(ReadList(await http.GetStringAsync(ReadOnlyMemo, ct), "readonlymemo.com")); }
        catch (HttpRequestException) { run.Log(ApiError.Of("ports.log.unreachable", ("source", "Read Only Memo")), "warn"); }

        var known = Ports.Links(db);
        var (added, _) = Ports.Save(db, sure.Where(e => !Ports.Known(known, e.Link)).ToList(), null);
        known = Ports.Links(db);

        // Lo dudoso que no está en la lista ni pasó ya por Claude.
        var consoles = Consoles(db);
        var todo = doubtful.Where(d => !Ports.Known(known, d.Link)).DistinctBy(d => d.Link.TrimEnd('/').ToLowerInvariant())
            .Where(d => ClaudeFingerprint.Changed(db, PromptName, Key(d), d.Link)).Take(MaxCandidates).ToList();
        var prompt = ClaudeRunner.Prompt(PromptName);
        var (verified, rejected) = (0, 0);
        for (var i = 0; i < todo.Count; i += Batch)
        {
            run.Report(10 + i * 85 / todo.Count, ApiError.Of("ports.search.verifying", ("done", i.ToString()), ("total", todo.Count.ToString())));
            var batch = todo.Skip(i).Take(Batch).ToList();
            var reply = await ClaudeRunner.Run<Reply>(db, run.Id, PromptName, prompt, new { consoles, candidates = batch }, ct, Model);
            var today = Ports.Today();
            var entries = new List<Ports.Entry>();
            foreach (var c in batch)
            {
                var v = reply.Value.Ports.FirstOrDefault(p => string.Equals(p.Link.TrimEnd('/'), c.Link.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                if (v is { Verified: true } && consoles.Contains(v.Console) && Kinds.Contains(v.Kind) && Statuses.Contains(v.Status)
                    && !string.IsNullOrWhiteSpace(v.Game) && !string.IsNullOrWhiteSpace(v.Project))
                {
                    entries.Add(new(v.Console, v.Game.Trim(), v.Project.Trim(), v.Kind, string.IsNullOrWhiteSpace(v.Toolchain) ? null : v.Toolchain.Trim(), v.Status, false,
                        c.Link, c.Source, today));
                    run.Log(ApiError.Of("ports.log.added", ("game", v.Game), ("console", v.Console), ("link", c.Link)));
                }
                else
                {
                    rejected++;
                    run.Log(ApiError.Of("ports.log.rejected", ("link", c.Link), ("reason", v?.Reason ?? "—")));
                }
                // Lo que Claude no respondió se vuelve a pedir la próxima vez.
                if (v is not null) ClaudeFingerprint.Save(db, PromptName, Key(c), c.Link);
            }
            verified += Ports.Save(db, entries, null).Added;
        }
        var (total, matched) = Ports.Match(db);
        run.Log(ApiError.Of("ports.log.search", ("added", (added + verified).ToString()), ("rejected", rejected.ToString()), ("total", total.ToString()),
            ("matched", matched.ToString())));
    }

    static string Key(Candidate c) => "port:" + c.Link.TrimEnd('/').ToLowerInvariant();

    static List<string> Consoles(Database db)
    {
        using var c = db.Open();
        return [.. c.Query<string>("SELECT id FROM console ORDER BY rowid"), "megadrive", "dreamcast", "arcade"];
    }

    /// <summary>Las consolas como las nombra el RSS de Recompendium.</summary>
    static readonly Dictionary<string, string> RssConsoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NES"] = "nes", ["SNES"] = "snes", ["N64"] = "n64", ["Nintendo 64"] = "n64", ["GameCube"] = "gc", ["Wii"] = "wii", ["Wii U"] = "wiiu",
        ["Switch"] = "switch", ["Switch 2"] = "switch2", ["Game Boy"] = "gb", ["Game Boy Color"] = "gb", ["GBA"] = "gba", ["Game Boy Advance"] = "gba",
        ["DS"] = "ds", ["NDS"] = "ds", ["3DS"] = "3ds", ["PS1"] = "ps1", ["PlayStation"] = "ps1", ["PS2"] = "ps2", ["PlayStation 2"] = "ps2",
        ["PS3"] = "ps3", ["PS4"] = "ps4", ["PSP"] = "psp", ["Xbox"] = "xbox", ["Xbox 360"] = "x360", ["Mega Drive"] = "megadrive", ["Genesis"] = "megadrive",
        ["Dreamcast"] = "dreamcast", ["Arcade"] = "arcade", ["PC"] = "pc",
    };

    /// <summary>
    /// Cada ítem dice «Consola · Estado · dueño/repo. …». Un recomp con consola conocida entra tal cual; un port (la primera parte es
    /// su tipo, no la consola) va a Claude. Las herramientas no son ports.
    /// </summary>
    internal static (List<Ports.Entry> Sure, List<Candidate> Doubtful) ReadRss(string xml)
    {
        var (sure, doubtful) = (new List<Ports.Entry>(), new List<Candidate>());
        foreach (var item in XDocument.Parse(xml).Descendants("item"))
        {
            var category = (string?)item.Element("category");
            var title = ((string?)item.Element("title"))?.Trim() ?? "";
            var description = ((string?)item.Element("description"))?.Trim() ?? "";
            if (category is not ("Recomp" or "Port") || RssLine().Match(description) is not { Success: true } m) continue;
            var link = $"https://github.com/{m.Groups[3].Value}";
            var added = DateTime.TryParse((string?)item.Element("pubDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
                ? at.ToString("yyyy-MM-dd") : Ports.Today();
            if (category == "Recomp" && RssConsoles.TryGetValue(m.Groups[1].Value.Trim(), out var console))
                // «Astral Chain (captainj-sparrow)»: el paréntesis es el autor, cuando hay más de uno del mismo juego.
                sure.Add(new(console, Author().Replace(title, ""), m.Groups[3].Value.Split('/')[^1], "recomp", null, Ports.Status(m.Groups[2].Value.Trim()), false,
                    link, "recompendium", added));
            else doubtful.Add(new(link, title, description.Length > 400 ? description[..400] : description, "recompendium.com"));
        }
        return (sure, doubtful);
    }

    /// <summary>Los enlaces a repositorios de una lista conocida, con la sección donde aparecen; las secciones sin ports se saltan.</summary>
    internal static List<Candidate> ReadList(string html, string source)
    {
        var list = new List<Candidate>();
        var section = "";
        foreach (Match m in HeadingOrLink().Matches(html))
        {
            if (m.Groups["heading"].Success)
            {
                section = WebUtility.HtmlDecode(m.Groups["heading"].Value).Trim();
                continue;
            }
            if (Regex.IsMatch(section, "without ports|reference list|what is|what's better", RegexOptions.IgnoreCase)) continue;
            var name = WebUtility.HtmlDecode(Regex.Replace(m.Groups["text"].Value, "<[^>]+>", "")).Trim();
            if (name.Length < 2 || name.Equals("github", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new(m.Groups["link"].Value, name, section, source));
        }
        return list;
    }

    [GeneratedRegex(@"^([^·]+)·([^·]+)·\s*([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)\.?(?:\s|$)")]
    private static partial Regex RssLine();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex Author();

    [GeneratedRegex("""<h[23][^>]*>(?<heading>[^<]*)|<a [^>]*href="(?<link>https://github\.com/[^"/]+/[^"/#?]+)/?"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex HeadingOrLink();
}

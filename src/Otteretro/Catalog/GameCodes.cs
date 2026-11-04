using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// «Completar códigos» (#46, decisión 278): el TitleID o el serial de cada versión, sin IA, para que Otteretro reconozca por ID. Sale de
/// tablas públicas: titledb (Switch), la de Switchbrew (Switch 2) y el serial de los DAT de libretro (el resto). Cada nombre de esas tablas
/// se cruza con los juegos de su consola; si coincide con uno solo, su código queda en esa versión.
/// </summary>
public static partial class GameCodes
{
    public const string Kind = "codes.fill";
    const string Libretro = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/";
    const string Titledb = "https://raw.githubusercontent.com/blawar/titledb/master/US.en.json";
    const string Switchbrew = "https://switchbrew.org/w/index.php?title=Switch_2:_Title_list/Games&action=raw";

    /// <summary>Los DAT de libretro con serial, por consola (los mismos que usa Otteretro para verificar).</summary>
    static readonly Dictionary<string, string[]> Dats = new()
    {
        ["n64"] = ["no-intro/Nintendo - Nintendo 64"],
        ["gba"] = ["no-intro/Nintendo - Game Boy Advance"],
        ["ds"] = ["no-intro/Nintendo - Nintendo DS"],
        ["3ds"] = ["no-intro/Nintendo - Nintendo 3DS"],
        ["vita"] = ["no-intro/Sony - PlayStation Vita"],
        ["gc"] = ["redump/Nintendo - GameCube"],
        ["wii"] = ["redump/Nintendo - Wii"],
        ["ps1"] = ["redump/Sony - PlayStation"],
        ["ps2"] = ["redump/Sony - PlayStation 2"],
        ["ps3"] = ["redump/Sony - PlayStation 3"],
        ["psp"] = ["redump/Sony - PlayStation Portable"],
    };

    /// <summary>Para las pruebas: responde en lugar de GitHub y Switchbrew.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    /// <summary>Las pruebas lo apagan: sin <see cref="Handler"/> no se baja nada (titledb pesa 90 MB).</summary>
    public static bool Download { get; set; } = true;

    public static long Enqueue(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = @Kind AND status IN ('queued', 'running') LIMIT 1", new { Kind }) ?? TaskQueue.Enqueue(db, Kind);
    }

    public static Task Fill(TaskRun run, CancellationToken ct) =>
        Map(run.Db, (progress, detail) => run.Report(progress, detail), (message, level) => run.Log(message, level), ct);

    /// <summary>El mapeo, para la tarea y para `catalogo codigos` (#46): cada tabla, su consola y cuántas versiones quedaron con código.</summary>
    public static async Task<(int Versions, int Codes)> Map(Database db, Action<int, ApiError> report, Action<ApiError, string> log, CancellationToken ct)
    {
        if (!Download && Handler is null) return (0, 0);
        using var http = Handler is null ? new HttpClient() : new HttpClient(Handler);
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OtteretroCatalogo");

        var sources = new List<(string Console, string Url, Func<Stream, CancellationToken, Task<List<(string Name, string Code)>>> Read)>
        {
            ("switch", Titledb, ReadTitledb),
            ("switch2", Switchbrew, async (s, c) => ReadSwitchbrew(await new StreamReader(s).ReadToEndAsync(c))),
        };
        foreach (var (console, files) in Dats)
            foreach (var file in files)
                sources.Add((console, Libretro + Uri.EscapeDataString(file).Replace("%2F", "/") + ".dat", async (s, c) => ReadDat(new StreamReader(s))));

        int found = 0, total = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            var (console, url, read) = sources[i];
            report(i * 100 / sources.Count, ApiError.Of("codes.reading", ("console", console)));
            List<(string Name, string Code)> rows;
            try
            {
                await using var stream = await http.GetStreamAsync(url, ct);
                rows = await read(stream, ct);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
            {
                log(ApiError.Of("codes.log.unreachable", ("console", console)), "warn");
                continue;
            }
            var (matched, codes) = Save(db, console, rows);
            found += matched;
            total += codes;
            log(ApiError.Of("codes.log.console", ("console", console), ("versions", matched.ToString()), ("codes", codes.ToString())), "info");
        }
        log(ApiError.Of("codes.log.done", ("versions", found.ToString()), ("codes", total.ToString())), "info");
        return (found, total);
    }

    /// <summary>Cruza los nombres con las versiones de la consola (solo si el nombre es de un juego) y guarda sus códigos.</summary>
    internal static (int Versions, int Codes) Save(Database db, string console, List<(string Name, string Code)> rows)
    {
        using var c = db.Open();
        var byName = new Dictionary<string, HashSet<long>>();
        foreach (var (id, es, original) in c.Query<(long, string, string?)>(
                     "SELECT v.id, g.name_es, g.original_name FROM version v JOIN game g ON g.id = v.game_id WHERE v.platform = @console", new { console }))
            foreach (var name in new[] { es, original }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalize(x!)).Distinct())
                (byName.TryGetValue(name, out var set) ? set : byName[name] = []).Add(id);
        var versions = new HashSet<long>();
        var codes = 0;
        using var t = c.BeginTransaction();
        foreach (var (name, code) in rows)
            if (byName.TryGetValue(Normalize(name), out var set) && set.Count == 1 && !string.IsNullOrWhiteSpace(code))
            {
                codes += c.Execute("INSERT OR IGNORE INTO version_code (version_id, code) VALUES (@version, @code)", new { version = set.First(), code = code.Trim().ToUpperInvariant() }, t);
                versions.Add(set.First());
            }
        t.Commit();
        return (versions.Count, codes);
    }

    record TitledbEntry([property: JsonPropertyName("id")] string? Id, [property: JsonPropertyName("name")] string? Name);

    /// <summary>titledb: solo los juegos base (los DLC y las actualizaciones van con ellos en Otteretro).</summary>
    static async Task<List<(string, string)>> ReadTitledb(Stream stream, CancellationToken ct) =>
        ((await JsonSerializer.DeserializeAsync<Dictionary<string, TitledbEntry>>(stream, cancellationToken: ct)) ?? [])
        .Values.Where(e => e.Id is { Length: 16 } id && id.EndsWith("000") && !string.IsNullOrWhiteSpace(e.Name))
        .Select(e => (e.Name!, e.Id!.ToUpperInvariant())).ToList();

    internal static List<(string, string)> ReadSwitchbrew(string wiki) =>
        SwitchbrewRow().Matches(wiki).Select(m => (m.Groups[2].Value, m.Groups[1].Value.ToUpperInvariant())).ToList();

    /// <summary>Un DAT de clrmamepro: el nombre de cada `game ( … )` y su serial (en el juego o en su `rom`).</summary>
    internal static List<(string, string)> ReadDat(TextReader reader)
    {
        var rows = new List<(string, string)>();
        string? name = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("game (", StringComparison.Ordinal)) name = null;
            foreach (Match m in Pair().Matches(line))
            {
                var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (m.Groups[1].Value == "name" && !line.StartsWith("rom (", StringComparison.Ordinal)) name = value;
                else if (m.Groups[1].Value == "serial" && name is not null)
                    foreach (var serial in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) rows.Add((name, serial));
            }
        }
        return rows.Distinct().ToList();
    }

    /// <summary>
    /// El nombre sin región, etiquetas, marcas ni signos, igual que en Otteretro: «Legend of Zelda, The - Ocarina of Time (USA) (Rev 1)» →
    /// «the legend of zelda ocarina of time». «… – Nintendo Switch 2 Edition» es el mismo juego.
    /// </summary>
    public static string Normalize(string name)
    {
        name = Edition().Replace(name, "");
        name = Article().Replace(Tags().Replace(name, " ").Trim(), "$2 $1$3").Replace("'", "").Replace("’", "");
        var plain = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                plain.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"^(.+?), (The|A|An)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Article();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s*([-–:]\s*)?(Nintendo Switch 2 Edition|for Nintendo Switch 2)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Edition();

    [GeneratedRegex("""(\w+) (?:"([^"]*)"|([^\s()]+))""")]
    private static partial Regex Pair();

    [GeneratedRegex(@"^\|\s*(0[14][0-9A-Fa-f]{14})\s*\|\|\s*([^|\n]+?)\s*\|\|", RegexOptions.Multiline)]
    private static partial Regex SwitchbrewRow();
}

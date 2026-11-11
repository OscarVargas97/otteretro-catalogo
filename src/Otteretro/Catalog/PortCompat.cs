using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Cómo corre en el emulador de referencia de su consola el juego de cada port (#70, de `compat.mjs`): las listas oficiales de
/// compatibilidad, con el mejor nivel entre versiones y regiones. 3 perfecto, 2 jugable, 1 parcial, 0 no funciona; null sin dato.
/// Las consolas sin lista por juego quedan con su emulador y sin nivel: su madurez es la `emulation_status` de la consola.
/// </summary>
public static partial class PortCompat
{
    /// <summary>El emulador de referencia de cada consola (de `datos/emuladores.json`).</summary>
    static readonly Dictionary<string, string> Emulators = new()
    {
        ["nes"] = "Mesen",
        ["snes"] = "bsnes / Mesen",
        ["n64"] = "ares / simple64",
        ["gc"] = "Dolphin",
        ["wii"] = "Dolphin",
        ["wiiu"] = "Cemu",
        ["switch"] = "Ryubing",
        ["gb"] = "SameBoy",
        ["gba"] = "mGBA",
        ["ds"] = "melonDS",
        ["3ds"] = "Azahar",
        ["ps1"] = "DuckStation",
        ["ps2"] = "PCSX2",
        ["ps3"] = "RPCS3",
        ["ps4"] = "shadPS4",
        ["psp"] = "PPSSPP",
        ["xbox"] = "xemu",
        ["x360"] = "Xenia Canary",
        ["megadrive"] = "Genesis Plus GX",
        ["dreamcast"] = "Flycast",
        ["arcade"] = "MAME / FBNeo",
    };

    /// <summary>Entre búsquedas de GitHub: 30 por minuto con token, 10 sin él. Las pruebas lo ponen en cero.</summary>
    public static TimeSpan Pause { get; set; } = TimeSpan.FromSeconds(2.2);

    /// <summary>Lo que espera tras un límite de GitHub (403 o 429) antes de reintentar.</summary>
    public static TimeSpan RateWait { get; set; } = TimeSpan.FromSeconds(60);

    sealed class Context(HttpClient http, string? token)
    {
        public HttpClient Http => http;
        public string? Token => token;
    }

    /// <summary>Una fuente: o carga la lista entera una vez (<c>Load</c>) o se consulta juego a juego (<c>Each</c>).</summary>
    record Source(Func<Context, CancellationToken, Task<Func<string, int?>>>? Load, Func<Context, string, CancellationToken, Task<int?>>? Each);

    static readonly Dictionary<string, Source> Sources = new()
    {
        ["gc"] = new(Dolphin, null),
        ["wii"] = new(Dolphin, null),
        ["ps2"] = new(Pcsx2, null),
        ["switch"] = new(Ryubing, null),
        ["xbox"] = new(Xemu, null),
        ["x360"] = new(null, Issues("xenia-project/game-compatibility", "state-", new() { ["playable"] = 2, ["gameplay"] = 1 })),
        ["ps4"] = new(null, Issues("shadps4-compatibility/shadps4-game-compatibility", "status-", new() { ["playable"] = 2, ["ingame"] = 1 })),
        ["ps3"] = new(null, Rpcs3),
        ["wiiu"] = new(null, Cemu),
    };

    /// <summary>Cada cuánto se vuelve a consultar un juego (#153).</summary>
    static readonly TimeSpan Recheck = TimeSpan.FromDays(30);

    /// <summary>
    /// Consulta los juegos nuevos o los de hace más de <see cref="Recheck"/> e informa el avance por consola en <paramref name="report"/>
    /// («Xbox 360: 120/210») y el porcentaje del total. Un juego cuya consulta falló queda sin fecha y se reintenta la próxima vez.
    /// </summary>
    public static async Task Fill(Database db, HttpClient http, string? token, Action<ApiError, string> log, Action<string, int, int, int>? report, CancellationToken ct)
    {
        var since = (DateTime.UtcNow - Recheck).ToString("O", CultureInfo.InvariantCulture);
        List<(string Console, string Game)> games;
        using (var c = db.Open())
            games = c.Query<(string, string)>("""
                SELECT console, game FROM port GROUP BY console, game
                HAVING min(coalesce(compat_at, '')) < @since ORDER BY console, game
                """, new { since }).ToList();
        var totals = games.CountBy(g => g.Console).ToDictionary();
        var context = new Context(http, string.IsNullOrWhiteSpace(token) ? null : token);
        var loaded = new Dictionary<string, Task<Func<string, int?>>>();
        int done = 0, count = 0;
        string? last = null;
        using var w = db.Open();
        foreach (var (console, game) in games)
        {
            done = last == console ? done + 1 : 1;
            last = console;
            report?.Invoke(console, done, totals[console], 100 * count++ / games.Count);
            int? level = null;
            var ok = true;
            if (Sources.TryGetValue(console, out var source))
                try
                {
                    if (source.Load is { } load)
                    {
                        // Una lista por emulador, aunque dos consolas la compartan (GameCube y Wii en Dolphin).
                        var emulator = Emulators[console];
                        if (!loaded.TryGetValue(emulator, out var lookup)) loaded[emulator] = lookup = load(context, ct);
                        level = (await lookup)(game);
                    }
                    else level = await source.Each!(context, game, ct);
                }
                catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    log(ApiError.Of("ports.log.compatFailed", ("console", console), ("game", game)), "warn");
                    loaded.Remove(Emulators[console]); // una lista que no bajó se intenta con el próximo juego
                    ok = false;
                }
            // Juego a juego: si se cancela, lo hecho queda (#155).
            w.Execute("""
                UPDATE port SET emulator = @emulator, emulator_level = CASE WHEN @ok THEN @level ELSE emulator_level END,
                  compat_at = CASE WHEN @ok THEN @now ELSE compat_at END
                WHERE console = @console AND game = @game
                """, new { console, game, emulator = Emulators.GetValueOrDefault(console), level, ok, now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        }
    }

    static async Task<string> Get(Context x, string url, CancellationToken ct, bool github = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (github && x.Token is not null) request.Headers.Authorization = new("Bearer", x.Token);
            using var response = await x.Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await Task.Delay(RateWait, ct);
                continue;
            }
            throw new HttpRequestException($"{(int)response.StatusCode} {url}", null, response.StatusCode);
        }
    }

    static async Task<JsonNode> Json(Context x, string url, CancellationToken ct, bool github = false) =>
        JsonNode.Parse(await Get(x, url, ct, github)) ?? throw new System.Text.Json.JsonException(url);

    // ---- Comparar títulos entre fuentes (de `compat.mjs`) ----

    /// <summary>Sin acentos, ™, paréntesis ni «the», y con los números romanos II a IV como cifras.</summary>
    public static string Norm(string s)
    {
        s = Regex.Replace(s, "[™®©]", "").Normalize(NormalizationForm.FormKD);
        var plain = new StringBuilder();
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) plain.Append(ch);
        s = Tags().Replace(plain.ToString().ToLowerInvariant(), " ").Replace("&", " and ");
        s = Regex.Replace(s, "[^a-z0-9: ]+", " ");
        s = Regex.Replace(Regex.Replace(Regex.Replace(s, @"\biv\b", "4"), @"\biii\b", "3"), @"\bii\b", "2");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.StartsWith("the ", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>El título completo y sin subtítulo.</summary>
    static (string Full, string Base) Keys(string title)
    {
        var n = Norm(title);
        static string Clean(string k) => Regex.Replace(k, @"\s+", " ").Trim();
        return (Clean(n.Replace(":", "")), Clean(n.Split(':')[0]));
    }

    /// <summary>Iguales, o uno es el otro sin subtítulo.</summary>
    public static bool Same(string a, string b)
    {
        var ((fa, ba), (fb, bb)) = (Keys(a), Keys(b));
        return fa == fb || fa == bb || ba == fb;
    }

    /// <summary>Lo que se busca: el título sin paréntesis ni subtítulo.</summary>
    static string Term(string title) => Regex.Replace(Regex.Replace(title, "[™®©]", ""), @"\([^)]*\)", "").Split(':')[0].Trim();

    /// <summary>Título → mejor nivel entre sus versiones y regiones.</summary>
    internal static Func<string, int?> Index(IEnumerable<(string Title, int Level)> pairs)
    {
        var map = new Dictionary<string, int>();
        foreach (var (title, level) in pairs)
        {
            var (f, b) = Keys(title);
            foreach (var k in new[] { f, b })
                if (!map.TryGetValue(k, out var old) || old < level) map[k] = level;
        }
        return title =>
        {
            var (f, b) = Keys(title);
            return map.TryGetValue(f, out var l) ? l : f != b && map.TryGetValue(b, out var lb) ? lb : null;
        };
    }

    // ---- Listas completas ----

    static async Task<Func<string, int?>> Dolphin(Context x, CancellationToken ct)
    {
        var pairs = new List<(string, int)>();
        foreach (var (stars, level) in new[] { (5, 3), (4, 2), (3, 1), (2, 0), (1, 0) })
        {
            var cont = "";
            do
            {
                var j = await Json(x, $"https://wiki.dolphin-emu.org/api.php?action=query&list=categorymembers&cmtitle=Category:{stars}_stars_(Rating)&cmlimit=500&format=json{cont}", ct);
                foreach (var p in j["query"]!["categorymembers"]!.AsArray()) pairs.Add((p!["title"]!.GetValue<string>(), level));
                cont = j["continue"]?["cmcontinue"]?.GetValue<string>() is { } next ? $"&cmcontinue={Uri.EscapeDataString(next)}" : "";
            } while (cont.Length > 0);
        }
        return Index(pairs);
    }

    /// <summary>GameIndex: compat 6 perfecto, 5 jugable, 4 en el juego; 1 a 3 no llega a jugarse.</summary>
    static async Task<Func<string, int?>> Pcsx2(Context x, CancellationToken ct)
    {
        var pairs = new List<(string, int)>();
        string? name = null;
        foreach (var line in (await Get(x, "https://raw.githubusercontent.com/PCSX2/pcsx2/master/bin/resources/GameIndex.yaml", ct)).Split('\n'))
        {
            if (Pcsx2Name().Match(line) is { Success: true } n) { name = n.Groups[1].Value; continue; }
            if (Pcsx2Compat().Match(line) is { Success: true } m && name is not null) pairs.Add((name, m.Groups[1].Value switch { "6" => 3, "5" => 2, "4" => 1, _ => 0 }));
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) name = null;
        }
        return Index(pairs);
    }

    static async Task<Func<string, int?>> Ryubing(Context x, CancellationToken ct) =>
        Index((await Get(x, "https://raw.githubusercontent.com/Ryubing/Compatibility/main/compatibility.csv", ct)).Split('\n').Skip(1)
            .Select(l => RyubingRow().Match(l)).Where(m => m.Success)
            .Select(m => (m.Groups[1].Value.Replace("\"\"", "\""), m.Groups[2].Value switch { "playable" => 2, "ingame" => 1, _ => 0 })));

    static async Task<Func<string, int?>> Xemu(Context x, CancellationToken ct) =>
        Index((await Json(x, "https://xemu.app/compat.json", ct)).AsArray()
            .Select(t => (t!["name"]!.GetValue<string>(), t["status"]?.GetValue<string>() switch { "Perfect" => 3, "Playable" => 2, _ => 0 })));

    // ---- Juego a juego ----

    /// <summary>Los reportes en issues de GitHub (Xenia, shadPS4): el mejor estado entre los que se llaman igual.</summary>
    static Func<Context, string, CancellationToken, Task<int?>> Issues(string repo, string prefix, Dictionary<string, int> levels) => async (x, title, ct) =>
    {
        await Task.Delay(x.Token is null ? Pause * 3 : Pause, ct);
        var q = Uri.EscapeDataString($"repo:{repo} in:title \"{Term(title)}\"");
        var j = await Json(x, $"https://api.github.com/search/issues?q={q}&per_page=30", ct, github: true);
        int? best = null;
        foreach (var item in j["items"]?.AsArray() ?? [])
        {
            var name = SerialPrefix().Replace(item!["title"]!.GetValue<string>(), ""); // «CUSA03173 - Bloodborne» → «Bloodborne»
            if (!Same(name, title)) continue;
            foreach (var label in item["labels"]?.AsArray() ?? [])
                if (label!["name"]?.GetValue<string>() is { } l && l.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var v = levels.GetValueOrDefault(l[prefix.Length..]);
                    if (best is null || v > best) best = v;
                }
        }
        return best;
    };

    static async Task<int?> Rpcs3(Context x, string title, CancellationToken ct)
    {
        var j = await Json(x, $"https://rpcs3.net/compatibility?api=v1&g={Uri.EscapeDataString(Term(title))}", ct);
        int? best = null;
        if (j["results"] is JsonObject results)
            foreach (var (_, r) in results)
                if (r?["title"]?.GetValue<string>() is { } t && Same(t, title))
                {
                    var v = r["status"]?.GetValue<string>() switch { "Playable" => 2, "Ingame" => 1, _ => 0 };
                    if (best is null || v > best) best = v;
                }
        return best;
    }

    static async Task<int?> Cemu(Context x, string title, CancellationToken ct)
    {
        var s = await Json(x, $"https://wiki.cemu.info/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(title)}&srlimit=5&format=json", ct);
        var hit = s["query"]?["search"]?.AsArray().Select(p => p!["title"]!.GetValue<string>()).FirstOrDefault(p => Same(p, title));
        if (hit is null) return null;
        var p = await Json(x, $"https://wiki.cemu.info/api.php?action=parse&page={Uri.EscapeDataString(hit)}&prop=wikitext&format=json&redirects=1", ct);
        // La primera fila es la versión de Cemu más reciente.
        return CemuRating().Match(p["parse"]?["wikitext"]?["*"]?.GetValue<string>() ?? "") is { Success: true } m
            ? m.Groups[1].Value.ToLowerInvariant() switch { "perfect" => 3, "playable" => 2, "runs" => 1, _ => 0 }
            : null;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Tags();

    [GeneratedRegex("^ {2}name: \"(.*)\"")]
    private static partial Regex Pcsx2Name();

    [GeneratedRegex(@"^ {2}compat: (\d)")]
    private static partial Regex Pcsx2Compat();

    [GeneratedRegex("^[^,]*,\"((?:[^\"]|\"\")*)\",[^,]*,([a-z]+),")]
    private static partial Regex RyubingRow();

    [GeneratedRegex(@"^[A-Z0-9]{8,9}\s*-\s*")]
    private static partial Regex SerialPrefix();

    [GeneratedRegex(@"rating\s*=\s*(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CemuRating();
}

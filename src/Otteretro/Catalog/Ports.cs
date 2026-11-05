using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="GameId">Su juego del catálogo; null si quedó sin juego.</param>
/// <param name="Manual">El juego lo eligió la persona: el cruce no lo cambia.</param>
public record PortInfo(long Id, string Console, string Game, string Project, string Kind, string Status, string Link, string Source, long? GameId, string? GameName, bool Manual);

/// <param name="Unmatched">Los que quedaron sin juego, para asignarlos a mano.</param>
/// <param name="ByStatus">Cuántos hay de cada estado (complete es «100 % jugable»), en el orden de <see cref="Ports.Statuses"/>.</param>
public record PortsPage(int Total, int Matched, List<PortInfo> Unmatched, Dictionary<string, int> ByStatus);

/// <param name="GameId">El juego; null lo deja otra vez en manos del cruce.</param>
public record AssignPortRequest(long? GameId);

/// <summary>
/// El catálogo de ports (#69, decisiones 299 y 300 de Otteretro), en dos pasos. (1) La lista de ports que existen, en `port` y sin tocar
/// los juegos: Recompendium, los extras del catálogo (`ports-extras.json`) y lo que agrega la búsqueda de nuevos (<see cref="PortSearch"/>).
/// (2) <see cref="Match"/> cruza cada uno con su juego por IGDB o por nombre y consola; lo que no cruza queda sin juego y se asigna a mano.
/// Reemplaza a `catalogo.mjs` de la persona, sin Node.
/// </summary>
public static partial class Ports
{
    public const string Kind = "ports.refresh";
    const string Recompendium = "https://codeload.github.com/Nio03/unricopie/tar.gz/HEAD";
    const string RecompendiumSite = "https://recompendium.com/en/";

    /// <summary>Para las pruebas: responde en lugar de GitHub, Recompendium y las listas de los emuladores.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    internal static HttpClient Http()
    {
        var http = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OtteretroCatalogo");
        return http;
    }

    public static long Enqueue(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = @Kind AND status IN ('queued', 'running') LIMIT 1", new { Kind }) ?? TaskQueue.Enqueue(db, Kind);
    }

    /// <summary>Un port como lo da su fuente.</summary>
    public record Entry(string Console, string Game, string Project, string Kind, string? Toolchain, string Status, bool Abandoned, string Link, string Source,
        string Added, long? IgdbId = null);

    /// <summary>«Ports» en Ajustes › Tareas: refresca la lista, la compatibilidad en los emuladores y vuelve a cruzar.</summary>
    public static async Task Refresh(TaskRun run, CancellationToken ct)
    {
        var today = Today();
        using var http = Http();
        run.Report(0, ApiError.Of("ports.reading"));
        List<Entry> list;
        await using (var stream = await http.GetStreamAsync(Recompendium, ct))
            list = await ReadRecompendium(stream, today, ct);
        var (added, delisted) = Save(run.Db, list, "recompendium");
        var (extras, _) = Save(run.Db, Extras(today), null);
        run.Log(ApiError.Of("ports.log.list", ("count", list.Count.ToString()), ("added", (added + extras).ToString()), ("delisted", delisted.ToString())));

        run.Report(30, ApiError.Of("ports.compat"));
        await PortCompat.Fill(run.Db, http, run.Credentials.Read("github.token"), (m, l) => run.Log(m, l), ct);

        run.Report(95, ApiError.Of("ports.matching"));
        var (total, matched) = Match(run.Db);
        run.Log(ApiError.Of("ports.log.done", ("total", total.ToString()), ("matched", matched.ToString())));
    }

    public static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    static string Key(string link) => link.Trim().TrimEnd('/').ToLowerInvariant();

    internal static HashSet<string> Links(Database db)
    {
        using var c = db.Open();
        return c.Query<string>("SELECT link FROM port").Select(Key).ToHashSet();
    }

    internal static bool Known(HashSet<string> links, string link) => links.Contains(Key(link));

    /// <summary>
    /// Suma a la lista lo que no estaba (por enlace) y pone al día lo que es de la misma fuente; lo de otra fuente no se toca. Con
    /// <paramref name="replace"/>, lo de esa fuente que ya no viene no se quita: queda marcado «ya no listado» y vuelve si reaparece.
    /// Una decompilación que pasa a tener port toma la fecha de hoy: es una novedad. Nunca toca el cruce.
    /// </summary>
    internal static (int Added, int Delisted) Save(Database db, List<Entry> entries, string? replace)
    {
        using var c = db.Open();
        var existing = c.Query<(long Id, string Link, string Source)>("SELECT id, link, source FROM port").ToDictionary(r => Key(r.Link));
        var seen = new HashSet<string>();
        int added = 0, delisted = 0;
        using var t = c.BeginTransaction();
        foreach (var e in entries)
        {
            var key = Key(e.Link);
            if (!seen.Add(key)) continue;
            if (!existing.TryGetValue(key, out var row))
            {
                c.Execute("""
                    INSERT INTO port (console, game, project, kind, toolchain, status, abandoned, link, source, added, igdb_id)
                    VALUES (@Console, @Game, @Project, @Kind, @Toolchain, @Status, @Abandoned, @Link, @Source, @Added, @IgdbId)
                    """, e, t);
                added++;
            }
            else if (row.Source == e.Source)
                c.Execute("""
                    UPDATE port SET console = @Console, game = @Game, project = @Project, kind = @Kind, toolchain = @Toolchain, status = @Status,
                        abandoned = @Abandoned, link = @Link, igdb_id = @IgdbId, delisted = 0,
                        added = CASE WHEN status = 'no-port' AND @Status <> 'no-port' THEN @Added ELSE added END
                    WHERE id = @Id
                    """, new { e.Console, e.Game, e.Project, e.Kind, e.Toolchain, e.Status, e.Abandoned, e.Link, e.IgdbId, e.Added, row.Id }, t);
        }
        if (replace is not null && entries.Count > 0)
            foreach (var (key, row) in existing.Where(x => x.Value.Source == replace && !seen.Contains(x.Key)))
                delisted += c.Execute("UPDATE port SET delisted = 1 WHERE id = @Id AND delisted = 0", new { row.Id }, t);
        t.Commit();
        return (added, delisted);
    }

    record ExtraEntry(string Console, string Game, string Project, string Kind, string? Toolchain, string Status, string Link, string Source, string? Added);

    /// <summary>Los ports que Recompendium no tiene (los 22 de la persona y los que se sumen): datos del catálogo, en el repo.</summary>
    public static List<Entry> Extras(string today)
    {
        using var stream = typeof(Ports).Assembly.GetManifestResourceStream("ports-extras.json")!;
        return JsonSerializer.Deserialize<List<ExtraEntry>>(stream, JsonSerializerOptions.Web)!
            .Select(x => new Entry(x.Console, x.Game, x.Project, x.Kind, x.Toolchain, x.Status, false, x.Link, x.Source, x.Added ?? today)).ToList();
    }

    /// <summary>Las consolas de Recompendium que el catálogo llama distinto; las que no tiene quedan con su clave (y sin juego).</summary>
    internal static string ConsoleId(string? key) => key switch
    {
        null or "" => "pc",
        "gamecube" => "gc",
        "nds" => "ds",
        "gbc" => "gb",
        "genesis" => "megadrive",
        _ => key,
    };

    /// <summary>
    /// Los estados, de mejor a peor. <see cref="Complete"/> es «100 % jugable»: se juega de principio a fin sin problemas importantes.
    /// playable es jugable con fallos; <see cref="NoPort"/>, una decompilación sin port todavía (se muestra, no se instala).
    /// </summary>
    public const string Complete = "complete", NoPort = "no-port";
    public static readonly string[] Statuses = [Complete, "playable", "beta", "experimental", NoPort];

    internal static string Status(string? status) => status?.ToLowerInvariant() switch
    {
        "fully" or "fully playable" or "stable" or "complete" => "complete",
        "playable" or "usable" => "playable",
        "beta" => "beta",
        _ => "experimental",
    };

    /// <summary>En los ports de Recompendium `name` es el proyecto: el juego real, para cruzarlo (de `catalogo.mjs`).</summary>
    static readonly Dictionary<string, string> PortGame = new()
    {
        ["2ship2harkinian"] = "The Legend of Zelda: Majora's Mask", ["3sxw"] = "Street Fighter III: 3rd Strike",
        ["banjo-kazooie-lighthouse"] = "Banjo-Kazooie", ["cannonball"] = "OutRun",
        ["diddy-kong-racing-golden-balloon-web"] = "Diddy Kong Racing", ["diddy-kong-racing-golden-balloon"] = "Diddy Kong Racing",
        ["dusklight"] = "The Legend of Zelda: Twilight Princess", ["halo-combat-evolved"] = "Halo: Combat Evolved",
        ["ladxhd-updated"] = "The Legend of Zelda: Link's Awakening DX", ["melee-pc"] = "Super Smash Bros. Melee",
        ["minish-cap-3ds"] = "The Legend of Zelda: The Minish Cap", ["minish-cap"] = "The Legend of Zelda: The Minish Cap",
        ["ocarina-of-time-psp"] = "The Legend of Zelda: Ocarina of Time", ["opengoal"] = "Jak and Daxter: The Precursor Legacy",
        ["p-06"] = "Sonic the Hedgehog (2006)", ["paper-mario-paperboat"] = "Paper Mario",
        ["pokemon-gen1"] = "Pokémon Red", ["project-reforged"] = "Sonic and the Black Knight",
        ["project-reignition"] = "Sonic and the Secret Rings", ["pt"] = "P.T.", ["render96ex"] = "Super Mario 64",
        ["ship-of-harkinian"] = "The Legend of Zelda: Ocarina of Time", ["sly-cooper-projectcane"] = "Sly Cooper and the Thievius Raccoonus",
        ["sm64-port"] = "Super Mario 64", ["sonic-cd-decomp"] = "Sonic CD", ["sonic-generations-decomp"] = "Sonic Generations",
        ["sonic-mania-decomp"] = "Sonic Mania", ["spaghettikart"] = "Mario Kart 64", ["star-fox-enhanced"] = "Star Fox",
        ["starship"] = "Star Fox 64", ["super-mario-64-ghostship"] = "Super Mario 64", ["super-smash-bros-battleship"] = "Super Smash Bros.",
        ["super-mario-bros-remastered"] = "Super Mario Bros.", ["f-zero-x"] = "F-Zero X", ["pikmin"] = "Pikmin", ["metroid-prime"] = "Metroid Prime",
        ["dragon-ball-z-budokai-tenkaichi-3"] = "Dragon Ball Z: Budokai Tenkaichi 3", ["crash-team-racing"] = "Crash Team Racing",
        ["silent-hill"] = "Silent Hill", ["syphon-filter"] = "Syphon Filter", ["ico"] = "ICO", ["bloodborne"] = "Bloodborne", ["rage-racer"] = "Rage Racer",
        ["animal-crossing"] = "Animal Crossing", ["super-mario-strikers"] = "Super Mario Strikers", ["super-mario-sunshine"] = "Super Mario Sunshine",
        ["perfect-dark"] = "Perfect Dark",
    };

    static readonly Dictionary<string, string> PortProject = new()
    {
        ["f-zero-x"] = "G-Diffuser", ["pikmin"] = "Open Nectar", ["dusklight"] = "Dusklight", ["paper-mario-paperboat"] = "PaperBoat",
        ["opengoal"] = "OpenGOAL", ["sly-cooper-projectcane"] = "ProjectCane", ["super-mario-64-ghostship"] = "Ghostship",
        ["super-smash-bros-battleship"] = "BattleShip", ["banjo-kazooie-lighthouse"] = "Lighthouse", ["diddy-kong-racing-golden-balloon"] = "Golden Balloon",
        ["diddy-kong-racing-golden-balloon-web"] = "Golden Balloon (WebGPU)", ["minish-cap"] = "Project Picori", ["pokemon-gen1"] = "Gen1Recomp",
        ["project-reforged"] = "Project Reforged", ["project-reignition"] = "Project Reignition", ["p-06"] = "Project '06",
        ["halo-combat-evolved"] = "Halo CE Universal", ["ladxhd-updated"] = "LADXHD Updated", ["ocarina-of-time-psp"] = "oot-PSP",
    };

    static readonly Dictionary<string, string> PortKind = new() { ["decomp-port"] = "decomp", ["fan-remake"] = "remake", ["native-layer"] = "native-layer" };

    /// <summary>
    /// El tar de `Nio03/unricopie`: cada YAML de `src/data/ports` y `src/data/recomps` es un port, y cada decompilación de `src/data/decomps`
    /// con consola entra como «sin port todavía». Las decompilaciones van al final: si comparten enlace con un port, gana el port.
    /// </summary>
    internal static async Task<List<Entry>> ReadRecompendium(Stream gz, string today, CancellationToken ct)
    {
        var list = new List<Entry>();
        await using var unzip = new GZipStream(gz, CompressionMode.Decompress);
        await using var tar = new TarReader(unzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.DataStream is null || DataFile().Match(entry.Name) is not { Success: true } m) continue;
            var y = Yaml(await new StreamReader(entry.DataStream).ReadToEndAsync(ct));
            var (isPort, slug) = (m.Groups[1].Value == "ports", m.Groups[2].Value);
            var name = y.GetValueOrDefault("name") ?? slug;
            var repo = y.GetValueOrDefault("repo");
            if (m.Groups[1].Value == "decomps")
            {
                var decomp = y.GetValueOrDefault("repoUrl") ?? (repo is { Length: > 0 } ? $"https://github.com/{repo}" : null) ?? y.GetValueOrDefault("homepage");
                if (y.GetValueOrDefault("console") is { } console && decomp is not null)
                    list.Add(new Entry(ConsoleId(console), name, (repo ?? slug).Split('/')[^1], "decomp", null, NoPort, y.GetValueOrDefault("abandoned") == "true",
                        decomp, "recompendium", today));
                continue;
            }
            var link = y.GetValueOrDefault("repoUrl") ?? (repo is { Length: > 0 } ? $"https://github.com/{repo}" : null) ?? y.GetValueOrDefault("homepage")
                ?? $"{RecompendiumSite}{(isPort ? "port" : "recomp")}/{slug}";
            var project = isPort ? PortProject.GetValueOrDefault(slug) ?? (repo is { Length: > 0 } ? repo.Split('/')[^1] : name) : (repo ?? slug).Split('/')[^1];
            list.Add(new Entry(ConsoleId(y.GetValueOrDefault("console")), isPort ? PortGame.GetValueOrDefault(slug, name) : name, project,
                isPort ? PortKind.GetValueOrDefault(y.GetValueOrDefault("category") ?? "", "port") : "recomp", isPort ? null : y.GetValueOrDefault("toolchain"),
                Status(y.GetValueOrDefault("status")), y.GetValueOrDefault("abandoned") == "true", link, "recompendium", today));
        }
        return list.OrderBy(e => e.Status == NoPort).ToList();
    }

    /// <summary>Solo las claves escalares de primer nivel, como `catalogo.mjs`.</summary>
    static Dictionary<string, string> Yaml(string text)
    {
        var o = new Dictionary<string, string>();
        foreach (Match m in YamlLine().Matches(text))
        {
            var v = m.Groups[2].Value.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Replace("\\\"", "\"");
            if (v.Length > 0) o[m.Groups[1].Value] = v;
        }
        return o;
    }

    /// <summary>
    /// El cruce (paso 2): cada port sin juego elegido a mano va con el juego de su consola que tiene su ID de IGDB o, si no, su mismo
    /// nombre (normalizado como los códigos). Solo si hay uno: con dos candidatos queda sin juego. Lo elegido a mano que apunta a un
    /// juego fusionado sigue al que lo reemplazó.
    /// </summary>
    public static (int Total, int Matched) Match(Database db)
    {
        using var c = db.Open();
        var byName = new Dictionary<(string, string), HashSet<long>>();
        var byIgdb = new Dictionary<long, long>();
        foreach (var (platform, id, es, original, igdb, versionIgdb) in c.Query<(string, long, string, string?, long?, long?)>(
                     "SELECT v.platform, g.id, g.name_es, g.original_name, g.igdb_id, v.igdb_id FROM version v JOIN game g ON g.id = v.game_id"))
        {
            foreach (var name in new[] { es, original }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => GameCodes.Normalize(x!)))
                (byName.TryGetValue((platform, name), out var set) ? set : byName[(platform, name)] = []).Add(id);
            if (igdb is { } i) byIgdb.TryAdd(i, id);
            if (versionIgdb is { } v) byIgdb.TryAdd(v, id);
        }
        var games = c.Query<long>("SELECT id FROM game").ToHashSet();
        var replaced = c.Query<(long Old, long? New)>("SELECT old_id, new_id FROM replaced WHERE kind = 'game'").ToDictionary(r => r.Old, r => r.New);

        using var t = c.BeginTransaction();
        foreach (var p in c.Query<(long Id, string Console, string Game, long? IgdbId, long? GameId, bool Manual)>(
                     "SELECT id, console, game, igdb_id, game_id, manual FROM port", transaction: t).ToList())
        {
            long? game;
            var manual = p.Manual;
            if (p.Manual)
            {
                game = p.GameId;
                for (var hops = 0; game is { } g && !games.Contains(g) && hops < 10; hops++) game = replaced.GetValueOrDefault(g);
                if (game is not { } found || !games.Contains(found)) (game, manual) = (null, false);
            }
            else game = p.IgdbId is { } i && byIgdb.TryGetValue(i, out var byId) ? byId
                : byName.TryGetValue((p.Console, GameCodes.Normalize(p.Game)), out var set) && set.Count == 1 ? set.First() : null;
            if (game != p.GameId || manual != p.Manual)
                c.Execute("UPDATE port SET game_id = @game, manual = @manual WHERE id = @Id", new { game, manual, p.Id }, t);
        }
        t.Commit();
        return ((int)c.ExecuteScalar<long>("SELECT count(*) FROM port"), (int)c.ExecuteScalar<long>("SELECT count(*) FROM port WHERE game_id IS NOT NULL"));
    }

    public static PortsPage Page(Database db)
    {
        using var c = db.Open();
        var unmatched = c.Query<(long Id, string Console, string Game, string Project, string Kind, string Status, string Link, string Source, bool Manual)>(
                "SELECT id, console, game, project, kind, status, link, source, manual FROM port WHERE game_id IS NULL ORDER BY console, game, project")
            .Select(p => new PortInfo(p.Id, p.Console, p.Game, p.Project, p.Kind, p.Status, p.Link, p.Source, null, null, p.Manual)).ToList();
        var counts = c.Query<(string Status, long Count)>("SELECT status, count(*) FROM port GROUP BY status").ToDictionary(r => r.Status, r => (int)r.Count);
        return new((int)c.ExecuteScalar<long>("SELECT count(*) FROM port"), (int)c.ExecuteScalar<long>("SELECT count(*) FROM port WHERE game_id IS NOT NULL"), unmatched,
            Statuses.ToDictionary(s => s, s => counts.GetValueOrDefault(s)));
    }

    /// <summary>Asignar a mano (o soltar, con null): false si el port o el juego no existen.</summary>
    public static bool Assign(Database db, long id, long? gameId)
    {
        using (var c = db.Open())
        {
            if (gameId is { } g && c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE id = @g", new { g }) == 0) return false;
            if (c.Execute("UPDATE port SET game_id = @gameId, manual = @manual WHERE id = @id", new { id, gameId, manual = gameId is not null }) == 0) return false;
        }
        if (gameId is null) Match(db);
        return true;
    }

    [GeneratedRegex(@"/src/data/(ports|recomps|decomps)/([^/]+)\.yaml$")]
    private static partial Regex DataFile();

    [GeneratedRegex(@"^([A-Za-z]+):[ \t]*(.*?)\r?$", RegexOptions.Multiline)]
    private static partial Regex YamlLine();
}

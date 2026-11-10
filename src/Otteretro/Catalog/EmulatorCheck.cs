using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// La tarea «Emuladores» (#97, decisiones 354 a 358, 370 y 371 de Otteretro), una sola: revisa cada receta y busca forks.
/// <para>Recetas: una release nueva que calza con el patrón se aplica sola (suma su descarga de Windows, si la receta lleva
/// <c>downloads</c>) y queda como cambio sin publicar; un patrón que ya no calza o un repo archivado, movido o desaparecido van a
/// Revisar con el motivo.</para>
/// <para>Forks: los del repo de cada emulador, la búsqueda por nombre en GitHub y las listas conocidas. Los filtros (355) van por
/// API, sin Claude; Sonnet solo resuelve lo que pasó los filtros pero no se sabe de qué receta es. Como <see cref="PortSearch"/>,
/// solo agrega: lo visto queda en <c>fork_seen</c> y no se repite ni se vuelve a pedir. Lo que pasa queda en Revisar (#98).</para>
/// </summary>
public static partial class EmulatorCheck
{
    public const string Kind = "emulators.check", PromptName = "verificar-fork", Model = "sonnet";

    /// <summary>Los dudosos que van a Claude en una ejecución; el resto queda para la próxima.</summary>
    public static int MaxCandidates { get; set; } = 25;
    const int Batch = 5, MinStars = 10;
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(183), Recheck = TimeSpan.FromDays(30);

    /// <summary>Entre búsquedas de GitHub (30 por minuto con clave). Las pruebas lo ponen en cero.</summary>
    public static TimeSpan SearchDelay { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Las listas conocidas de emuladores (elegidas con la persona): de cada una salen los enlaces a repos.</summary>
    static readonly (string Name, string Url)[] Lists =
    [
        ("alnacle/awesome-emulators", "https://raw.githubusercontent.com/alnacle/awesome-emulators/HEAD/README.md"),
        ("DerekTurtleRoe/awesome-emulators", "https://raw.githubusercontent.com/DerekTurtleRoe/awesome-emulators/HEAD/README.md"),
        ("enhansome/enhansome-emulators", "https://raw.githubusercontent.com/enhansome/enhansome-emulators/HEAD/README.md"),
        ("Thorium/awesome-emulators", "https://raw.githubusercontent.com/Thorium/awesome-emulators/HEAD/README.md"),
    ];

    /// <summary>Las páginas de Emulation General Wiki de cada consola de las recetas (su texto de wiki, con los enlaces).</summary>
    static readonly Dictionary<string, string> WikiPages = new()
    {
        ["3ds"] = "Nintendo_3DS_emulators", ["dc"] = "Dreamcast_emulators", ["ds"] = "Nintendo_DS_emulators", ["gb"] = "Game_Boy_emulators",
        ["gba"] = "Game_Boy_Advance_emulators", ["gc"] = "GameCube_emulators", ["wii"] = "Wii_emulators", ["genesis-slash-megadrive"] = "Sega_Genesis_emulators",
        ["n64"] = "Nintendo_64_emulators", ["nes"] = "NES_emulators", ["snes"] = "SNES_emulators", ["ps1"] = "PlayStation_emulators",
        ["ps2"] = "PlayStation_2_emulators", ["ps3"] = "PlayStation_3_emulators", ["ps4"] = "PlayStation_4_emulators", ["ps5"] = "PlayStation_5_emulators",
        ["psp"] = "PlayStation_Portable_emulators", ["vita"] = "PlayStation_Vita_emulators", ["switch"] = "Nintendo_Switch_emulators",
        ["switch2"] = "Nintendo_Switch_2_emulators", ["wiiu"] = "Wii_U_emulators", ["xbox"] = "Xbox_emulators", ["x360"] = "Xbox_360_emulators",
    };

    public static long Enqueue(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM task WHERE kind = @Kind AND status IN ('queued', 'running') LIMIT 1", new { Kind }) ?? TaskQueue.Enqueue(db, Kind);
    }

    /// <summary>Lo que espera en Revisar de esta tarea (recetas y forks): su contador en Ajustes › Tareas.</summary>
    public static int Pending(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<int>("SELECT count(*) FROM review_item WHERE entity IN ('recipe', 'fork') AND status = 'open'");
    }

    public record Recommendation(string Game, string Source);
    /// <param name="Recipe">El id de la receta de la que es fork o variante; vacío si no es de ninguna.</param>
    public record Verdict(string Link, bool Fork, string Recipe, List<Recommendation> RecommendedFor, string Reason);
    public record Reply(List<Verdict> Forks);

    record Recipe(string Id, string Name, string Kind, string Source, string? Host, string Repo, List<string> Consoles, JsonObject Json);

    /// <param name="Parent">El repo del que es fork (GitHub lo trae solo al pedir el repo; Gitea, siempre).</param>
    record Meta(string FullName, int Stars, bool Archived, bool Fork, string? Parent, DateTime? Created, DateTime? Pushed, string? Description);

    /// <param name="Recipe">La receta de la que seguro es fork (salió de sus forks).</param>
    /// <param name="Guess">La receta que se buscó por nombre: una pista para Claude.</param>
    record Candidate(string Source, string? Host, string Repo, string? Recipe, string? Guess, string FoundBy, string? Hint, Meta? Meta)
    {
        public string Key => $"{Host ?? "github.com"}/{Repo}".ToLowerInvariant();
        public string Url => $"https://{Host ?? "github.com"}/{Repo}";
    }

    /// <summary>Por qué un fork pasó los filtros (355): lo que se muestra en Revisar.</summary>
    record Passed(int Stars, string Tag, string? PublishedAt, string File, bool IsFork);

    public static async Task Run(TaskRun run, CancellationToken ct)
    {
        var db = run.Db;
        var token = run.Credentials.Read("github.token") is { Length: > 0 } t ? t : null;
        var recipes = Load(db);

        // 1. Cada receta.
        var (updated, problems) = (0, 0);
        for (var i = 0; i < recipes.Count; i++)
        {
            run.Report(i * 40 / Math.Max(recipes.Count, 1), ApiError.Of("emulators.checking", ("done", i.ToString()), ("total", recipes.Count.ToString())));
            var r = recipes[i];
            try
            {
                switch (await Check(db, run, r, token, ct))
                {
                    case true: updated++; break;
                    case false: problems++; break;
                }
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                run.Log(ApiError.Of("emulators.log.failed", ("repo", r.Repo), ("detail", e.Message)), "warn");
            }
        }

        // 2. Forks: juntar, filtrar por API y lo dudoso a Claude.
        var forks = await SearchForks(run, recipes, token, ct);
        run.Log(ApiError.Of("emulators.log.done", ("updated", updated.ToString()), ("problems", problems.ToString()), ("forks", forks.ToString())));
    }

    static List<Recipe> Load(Database db)
    {
        using var c = db.Open();
        return c.Query<string>("SELECT json FROM recipe ORDER BY id").Select(json => JsonNode.Parse(json)!.AsObject())
            .Select(j => (j, source: (string?)j["updates"]?["source"], repo: (string?)j["updates"]?["repo"], host: (string?)j["updates"]?["host"]))
            .Where(x => x.repo is not null && (x.source == "github" || x.source == "forgejo" && x.host is not null))
            .Select(x => new Recipe((string)x.j["id"]!, (string?)x.j["name"] ?? "", (string?)x.j["kind"] ?? "", x.source!, x.source == "forgejo" ? x.host : null, x.repo!,
                x.j["consoles"]?.AsArray().Select(k => (string)k!).ToList() ?? [], x.j))
            .ToList();
    }

    static string ApiBase(string source, string? host) => source == "forgejo" ? $"https://{host}/api/v1" : "https://api.github.com";

    /// <summary>
    /// Revisa una receta: true si aplicó una release nueva, false si fue a Revisar, null si está al día. El repo se pide directo: si
    /// se movió, la API redirige y devuelve otro nombre.
    /// </summary>
    static async Task<bool?> Check(Database db, TaskRun run, Recipe r, string? token, CancellationToken ct)
    {
        if (await Api(ApiBase(r.Source, r.Host) + "/repos/" + r.Repo, token, ct) is not { } node)
            return Problem(db, run, r, "gone", new() { ["repo"] = r.Repo });
        var meta = ReadMeta(node);
        if (!string.Equals(meta.FullName, r.Repo, StringComparison.OrdinalIgnoreCase))
            return Problem(db, run, r, "moved", new() { ["repo"] = r.Repo, ["to"] = meta.FullName });
        if (meta.Archived) return Problem(db, run, r, "archived", new() { ["repo"] = r.Repo });

        var releases = await Recipes.Releases(r.Source, r.Host, r.Repo, token, ct);
        // Las prereleases son de desarrollo: cuentan solo en una receta que las sigue.
        var dev = r.Json["devOnly"]?.GetValue<bool>() == true || r.Json["updates"]?["dev"]?.GetValue<bool>() == true;
        var latest = dev ? releases.FirstOrDefault() : releases.FirstOrDefault(x => !x.Prerelease) ?? releases.FirstOrDefault();
        if (latest is null) return Problem(db, run, r, "noRelease", new() { ["repo"] = r.Repo });
        if ((string?)r.Json["asset"] is not { } pattern) return null;
        if (latest.Assets.FirstOrDefault(a => Regex.IsMatch(a, pattern)) is not { } file)
        {
            // Si la release tiene un solo archivo de Windows, el patrón que saldría de él; si no, lo elige la persona en Revisar.
            var windows = latest.Assets.Where(IsWindows).ToList();
            return Problem(db, run, r, "noAsset", new()
            {
                ["repo"] = r.Repo, ["tag"] = latest.Tag, ["pattern"] = pattern, ["files"] = new JsonArray([.. latest.Assets.Select(a => JsonValue.Create(a))]),
                ["proposed"] = windows.Count == 1 ? Recipes.AssetPattern(windows[0]) : null,
            });
        }
        return await AddDownload(db, run, r, latest, file, ct) ? true : null;
    }

    /// <summary>
    /// Lo que sí se aplica solo: una receta que lleva <c>downloads</c> de Windows suma la de la release nueva, con su hash. Queda en la
    /// base local, sin publicar: Publicar la ofrece. Una receta sin <c>downloads</c> no fija versión y no hay nada que cambiar.
    /// </summary>
    static async Task<bool> AddDownload(Database db, TaskRun run, Recipe r, Recipes.Release latest, string file, CancellationToken ct)
    {
        if (r.Json["downloads"] is not JsonArray downloads) return false;
        var windows = downloads.Where(d => (string?)d?["system"] == "windows").ToList();
        if (windows.Count == 0 || windows.Any(d => (string?)d!["version"] == latest.Tag) || latest.Urls?.GetValueOrDefault(file) is not { } url) return false;
        using var response = await Recipes.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)response.StatusCode}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var hash = "sha256-" + Convert.ToBase64String(await SHA256.HashDataAsync(stream, ct));
        downloads.Add(new JsonObject { ["version"] = latest.Tag, ["system"] = "windows", ["url"] = url, ["hash"] = hash });
        var text = r.Json.ToJsonString(Recipes.Indented).ReplaceLineEndings("\n") + "\n";
        using (var doc = JsonDocument.Parse(text))
        {
            if (Published.CheckRecipe(doc.RootElement).Count > 0) return false; // no se guarda nada que no cumpla el esquema
            using var c = db.Open();
            Recipes.Store(c, doc.RootElement, text);
        }
        run.Log(ApiError.Of("emulators.log.updated", ("recipe", r.Id), ("tag", latest.Tag)));
        return true;
    }

    /// <summary>A Revisar, con el motivo; si ya espera ahí por lo mismo, no se repite. Siempre false (fue a Revisar).</summary>
    static bool Problem(Database db, TaskRun run, Recipe r, string reason, JsonObject detail)
    {
        using var c = db.Open();
        var open = c.ExecuteScalar<long>("""
            SELECT count(*) FROM review_item WHERE entity = 'recipe' AND status = 'open'
              AND json_extract(data, '$.recipe') = @Id AND json_extract(data, '$.reason') = @reason
            """, new { r.Id, reason });
        if (open == 0)
        {
            detail["recipe"] = r.Id;
            detail["reason"] = reason;
            c.Execute("INSERT INTO review_item (kind, entity, data, created_at) VALUES ('doubtful', 'recipe', @data, @now)",
                new { data = detail.ToJsonString(), now = DateTime.UtcNow.ToString("O") });
        }
        run.Log(ApiError.Of($"emulators.log.{reason}", ("recipe", r.Id), ("repo", r.Repo), ("to", (string?)detail["to"] ?? ""), ("tag", (string?)detail["tag"] ?? ""),
            ("pattern", (string?)detail["pattern"] ?? "")), "warn");
        return false;
    }

    static async Task<int> SearchForks(TaskRun run, List<Recipe> recipes, string? token, CancellationToken ct)
    {
        var db = run.Db;
        var emulators = recipes.Where(r => r.Kind == "emulator").ToList();
        run.Report(40, ApiError.Of("emulators.searching"));
        var found = new List<Candidate>();
        // Con el límite de consultas de GitHub se sigue con lo juntado: lo que falta se busca la próxima vez.
        var limited = false;
        foreach (var r in emulators)
        {
            if (limited) break;
            try
            {
                // Los forks de su repo (los 100 con más estrellas; Gitea no ordena, trae 50).
                var url = r.Source == "forgejo" ? $"{ApiBase(r.Source, r.Host)}/repos/{r.Repo}/forks?limit=50"
                    : $"https://api.github.com/repos/{r.Repo}/forks?sort=stargazers&per_page=100";
                if (await Api(url, token, ct) is JsonArray list)
                    found.AddRange(list.OfType<JsonObject>().Select(ReadMeta).Select(m => new Candidate(r.Source, r.Host, m.FullName, r.Id, null, "fork", null, m)));
                // Por nombre en GitHub: también lo que no es un fork técnico (Eden sale de yuzu).
                await Task.Delay(SearchDelay, ct);
                var q = Uri.EscapeDataString($"{r.Name} in:name fork:true");
                if ((await Api($"https://api.github.com/search/repositories?q={q}&sort=stars&order=desc&per_page=10", token, ct))?["items"] is JsonArray items)
                    found.AddRange(items.OfType<JsonObject>().Select(ReadMeta).Select(m => new Candidate("github", null, m.FullName, null, r.Id, "search", r.Name, m)));
            }
            catch (HttpRequestException e) when (IsLimit(e)) { limited = true; }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                run.Log(ApiError.Of("emulators.log.failed", ("repo", r.Repo), ("detail", e.Message)), "warn");
            }
        }
        var hosts = recipes.Where(r => r.Host is not null).Select(r => r.Host!).Append("github.com").Append("codeberg.org").ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var http = Ports.Http())
        {
            var sources = Lists.Concat(emulators.SelectMany(r => r.Consoles).Distinct().Where(WikiPages.ContainsKey)
                .Select(k => ("emulation.gametechwiki.com/" + WikiPages[k], $"https://emulation.gametechwiki.com/index.php?title={WikiPages[k]}&action=raw")));
            foreach (var (name, url) in sources)
                try { found.AddRange(ReadLinks(await http.GetStringAsync(url, ct), name, hosts)); }
                catch (HttpRequestException) { run.Log(ApiError.Of("emulators.log.unreachable", ("source", name)), "warn"); }
        }

        // Lo nuevo: ni una receta, ni ya visto (lo que no pasó los filtros se vuelve a mirar pasado un tiempo).
        var own = recipes.Select(r => $"{r.Host ?? "github.com"}/{r.Repo}".ToLowerInvariant()).ToHashSet();
        Dictionary<string, (string Result, string At)> seen;
        using (var c = db.Open()) seen = c.Query<(string Repo, string Result, string At)>("SELECT repo, result, checked_at FROM fork_seen").ToDictionary(x => x.Repo, x => (x.Result, x.At));
        var now = DateTime.UtcNow;
        bool Fresh(Candidate x) => !seen.TryGetValue(x.Key, out var s)
            || s.Result == "filtered" && DateTime.Parse(s.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) < now - Recheck;
        // Si aparece por varias vías, gana la que dice de qué receta es.
        var todo = found.Where(x => !own.Contains(x.Key) && Fresh(x)).OrderBy(x => x.Recipe is null).DistinctBy(x => x.Key).ToList();

        var (sure, doubtful) = (new List<(Candidate, Passed)>(), new List<(Candidate, Passed, Meta)>());
        for (var i = 0; i < todo.Count && !limited; i++)
        {
            run.Report(50 + i * 35 / todo.Count, ApiError.Of("emulators.filtering", ("done", i.ToString()), ("total", todo.Count.ToString())));
            var x = todo[i];
            try
            {
                var meta = x.Meta ?? (await Api(ApiBase(x.Source, x.Host) + "/repos/" + x.Repo, token, ct) is { } n ? ReadMeta(n) : null);
                if (meta is null) continue; // un enlace roto de una lista
                var (passed, why) = await Filter(x, meta, token, ct);
                if (passed is null) { Seen(db, x.Key, "filtered", why); continue; }
                // De qué receta es: la de sus forks o, si GitHub lo marca fork de una, esa. Lo demás es dudoso.
                if (x.Recipe is null && meta.Fork && meta.Parent is null && x.Source == "github" && await Api("https://api.github.com/repos/" + x.Repo, token, ct) is { } full)
                    meta = ReadMeta(full);
                var parent = meta.Parent is { } p ? recipes.FirstOrDefault(r => string.Equals(r.Repo, p, StringComparison.OrdinalIgnoreCase) && r.Host == x.Host) : null;
                if ((x.Recipe ?? parent?.Id) is { } recipe) sure.Add((x with { Recipe = recipe }, passed));
                else doubtful.Add((x, passed, meta));
            }
            catch (HttpRequestException e) when (IsLimit(e)) { limited = true; }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                run.Log(ApiError.Of("emulators.log.failed", ("repo", x.Repo), ("detail", e.Message)), "warn");
            }
        }

        if (limited) run.Log(ApiError.Of("emulators.log.rateLimited"), "warn");
        var added = 0;
        foreach (var (x, passed) in sure) added += ToReview(db, run, x, passed, [], null);

        // Lo dudoso, a Sonnet de a pocos; lo que no respondió se vuelve a pedir la próxima vez.
        var ask = doubtful.Take(MaxCandidates).ToList();
        var prompt = ClaudeRunner.Prompt(PromptName);
        var catalog = emulators.Select(r => new { id = r.Id, name = r.Name, repo = $"https://{r.Host ?? "github.com"}/{r.Repo}", consoles = r.Consoles }).ToList();
        for (var i = 0; i < ask.Count; i += Batch)
        {
            run.Report(85 + i * 15 / ask.Count, ApiError.Of("emulators.verifying", ("done", i.ToString()), ("total", ask.Count.ToString())));
            var batch = ask.Skip(i).Take(Batch).ToList();
            var candidates = batch.Select(b => new { link = b.Item1.Url, name = b.Item3.FullName, description = b.Item3.Description ?? "", hint = b.Item1.Hint ?? "",
                foundBy = b.Item1.FoundBy, guess = b.Item1.Guess ?? "" }).ToList();
            var reply = await ClaudeRunner.Run<Reply>(db, run.Id, PromptName, prompt, new { recipes = catalog, candidates }, ct, Model);
            foreach (var (x, passed, _) in batch)
            {
                var v = reply.Value.Forks.FirstOrDefault(f => string.Equals(f.Link.TrimEnd('/'), x.Url, StringComparison.OrdinalIgnoreCase));
                if (v is null) continue;
                if (v.Fork && emulators.Any(r => r.Id == v.Recipe)) added += ToReview(db, run, x with { Recipe = v.Recipe }, passed, v.RecommendedFor, v.Reason);
                else
                {
                    Seen(db, x.Key, "rejected", v.Reason);
                    run.Log(ApiError.Of("emulators.log.rejected", ("repo", x.Repo), ("reason", v.Reason)));
                }
            }
        }
        return added;
    }

    /// <summary>
    /// Los filtros de la decisión 355, del más barato al más caro: no archivado, al menos 10 estrellas, commits propios y una release con
    /// archivo de Windows de los últimos 6 meses. Devuelve por qué pasó o, si no, el motivo.
    /// </summary>
    static async Task<(Passed? Passed, string Why)> Filter(Candidate x, Meta meta, string? token, CancellationToken ct)
    {
        if (meta.Archived) return (null, "archived");
        if (meta.Stars < MinStars) return (null, "stars");
        // ponytail: «commits propios» = se le hizo push después de crearse el fork (GitHub no actualiza pushed_at sin push). Comparar
        // ramas con la API de compare es más exacto, pero cuesta una consulta más por fork.
        if (meta.Fork && !(meta.Pushed > meta.Created?.AddMinutes(1))) return (null, "noCommits");
        var release = (await Recipes.Releases(x.Source, x.Host, x.Repo, token, ct)).FirstOrDefault(r => r.Assets.Any(IsWindows));
        if (release is null) return (null, "noWindows");
        if (!DateTime.TryParse(release.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at) || at < DateTime.UtcNow - MaxAge)
            return (null, "old");
        return (new Passed(meta.Stars, release.Tag, release.PublishedAt, release.Assets.First(IsWindows), meta.Fork), "");
    }

    /// <summary>Un archivo de Windows: dice serlo y no es de otro sistema ni de depuración.</summary>
    internal static bool IsWindows(string file) => !Recipes.NotWindows().IsMatch(file) && WindowsFile().IsMatch(file);

    /// <summary>
    /// Un candidato a Revisar (#98 lo muestra y lo acepta): su repo, la receta original, por qué pasó los filtros y los juegos para los
    /// que la comunidad lo recomienda, con su fuente. Devuelve 1.
    /// </summary>
    static int ToReview(Database db, TaskRun run, Candidate x, Passed passed, List<Recommendation> recommended, string? reason)
    {
        var data = new
        {
            repo = x.Repo, url = x.Url, source = x.Source, host = x.Host, recipe = x.Recipe, foundBy = x.FoundBy, hint = x.Hint,
            passed, recommendedFor = recommended, reason,
        };
        using (var c = db.Open())
            c.Execute("INSERT INTO review_item (kind, entity, data, created_at) VALUES ('doubtful', 'fork', @data, @now)",
                new { data = JsonSerializer.Serialize(data, JsonSerializerOptions.Web), now = DateTime.UtcNow.ToString("O") });
        Seen(db, x.Key, "review", null);
        run.Log(ApiError.Of("emulators.log.candidate", ("repo", x.Repo), ("recipe", x.Recipe!)));
        return 1;
    }

    static void Seen(Database db, string repo, string result, string? reason)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO fork_seen (repo, result, reason, checked_at) VALUES (@repo, @result, @reason, @now)
            ON CONFLICT (repo) DO UPDATE SET result = excluded.result, reason = excluded.reason, checked_at = excluded.checked_at
            """, new { repo, result, reason, now = DateTime.UtcNow.ToString("O") });
    }

    static bool IsLimit(HttpRequestException e) => e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    static Task<JsonNode?> Api(string url, string? token, CancellationToken ct) => Recipes.Api(url, token, ct);

    /// <summary>Un repo como lo da GitHub o Gitea (los nombres de algunos campos cambian).</summary>
    static Meta ReadMeta(JsonNode j) => new((string)j["full_name"]!, (int?)(j["stargazers_count"] ?? j["stars_count"]) ?? 0, (bool?)j["archived"] ?? false,
        (bool?)j["fork"] ?? false, (string?)(j["source"]?["full_name"] ?? j["parent"]?["full_name"]), Date(j["created_at"]), Date(j["pushed_at"] ?? j["updated_at"]),
        (string?)j["description"]);

    static DateTime? Date(JsonNode? n) =>
        DateTime.TryParse((string?)n, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>Los enlaces a repos (GitHub, Codeberg y los servidores de las recetas) de una lista, con su línea como pista.</summary>
    static IEnumerable<Candidate> ReadLinks(string text, string source, HashSet<string> hosts) =>
        text.Split('\n').SelectMany(line => RepoLink().Matches(line).Select(m => (m, line)))
            .Where(x => hosts.Contains(x.m.Groups["host"].Value) && !NotRepo().IsMatch(x.m.Groups["owner"].Value))
            .Select(x =>
            {
                var host = x.m.Groups["host"].Value.ToLowerInvariant();
                var name = Regex.Replace(x.m.Groups["name"].Value, @"(\.git)?\.?$", "");
                var hint = x.line.Trim();
                return new Candidate(host == "github.com" ? "github" : "forgejo", host == "github.com" ? null : host, $"{x.m.Groups["owner"].Value}/{name}", null, null,
                    "list:" + source, hint.Length > 300 ? hint[..300] : hint, null);
            });

    [GeneratedRegex(@"https?://(?:www\.)?(?<host>[A-Za-z0-9.-]+\.[A-Za-z]+)/(?<owner>[A-Za-z0-9_.-]+)/(?<name>[A-Za-z0-9_.-]+)")]
    private static partial Regex RepoLink();

    /// <summary>Rutas de GitHub que no son un dueño.</summary>
    [GeneratedRegex("^(sponsors|topics|orgs|features|marketplace|collections|apps|settings|about|login|search|users)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotRepo();

    [GeneratedRegex(@"win|\.exe$|msvc|mingw", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsFile();
}

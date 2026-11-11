using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Las recetas de los ports (#119, solicitud #118): un paso de la tarea «Ports», después del cruce y sin Claude. Cada port con
/// juego cuyo enlace es un repo de GitHub o Forgejo pide su última release; con archivos de Windows, se crea su receta mínima
/// (<c>kind: port</c>, su fila, fuente y el patrón del archivo de cada sistema). Todo queda en Emuladores sin publicar
/// (otteretro-catalogo#153): sin un archivo claro, la receta va sin él y con el aviso «Elegir el archivo» en su tarjeta. Lo visto queda en
/// <c>port_recipe_seen</c>; lo que no tiene archivos de Windows ni de Linux se vuelve a mirar en cada pasada. Un repo que ya es fuente de una
/// receta no se propone.
/// </summary>
public static class PortRecipes
{
    record Data(long Port, string Game, string Project, string Console, string Folder, string Source, string? Host, string Repo, string Url, string Tag,
        string? PublishedAt, List<string> Files, string? Link = null);

    static string Key(string? host, string repo) => $"{host ?? "github.com"}/{repo}".ToLowerInvariant();

    /// <summary>Convierte en receta lo que esperaba en Revisar (de antes de otteretro-catalogo#153). «Ports» lo hace al empezar (#155).</summary>
    public static async Task<int> AcceptOpen(Database db)
    {
        var created = 0;
        List<(long Id, string Data)> open;
        using (var c = db.Open()) open = c.Query<(long, string)>("SELECT id, data FROM review_item WHERE entity = 'port' AND status = 'open' ORDER BY id").ToList();
        foreach (var (id, json) in open)
        {
            var d = Read(json);
            if (Ports.ByLink(db, d.Link ?? d.Url) is not null && await Create(db, d, d.Files, []) is null) created++;
            using var c = db.Open();
            c.Execute("UPDATE review_item SET status = 'approved' WHERE id = @id", new { id });
            Seen(db, Key(d.Host, d.Repo), d.Tag, "accepted");
        }
        return created;
    }

    /// <summary>
    /// El paso de la tarea: crea la receta de cada port con una release de Windows o de Linux. Antes convierte lo que esperaba en Revisar
    /// (de antes de otteretro-catalogo#153). Devuelve cuántas recetas creó.
    /// </summary>
    public static async Task<int> Propose(TaskRun run, CancellationToken ct)
    {
        var db = run.Db;
        var created = await AcceptOpen(db);

        var token = run.Credentials.Read("github.token") is { Length: > 0 } t ? t : null;
        HashSet<string> own, ownRepos; // los ports que ya tienen receta: por su enlace (#149) o, en una receta sin «port», por su repo
        // ponytail: Forgejo solo en Codeberg y los servidores de las recetas; otro enlace (una web) no es un repo: su repo lo busca Claude (#155).
        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase) { "codeberg.org" };
        Dictionary<string, (string? Tag, string Result)> seen;
        List<(long Id, string Console, string Game, string Project, string Link, string Folder, string? Repo)> ports;
        using (var c = db.Open())
        {
            var recipes = c.Query<string>("SELECT json FROM recipe").Select(j => JsonNode.Parse(j)!).ToList();
            own = recipes.Select(r => (string?)r["port"]).OfType<string>().Select(Ports.Key).ToHashSet();
            var sources = recipes.Select(r => r["updates"])
                .Where(u => (string?)u?["repo"] is not null).Select(u => ((string?)u!["host"], (string)u["repo"]!)).ToList();
            ownRepos = sources.Select(s => Key(s.Item1, s.Item2)).ToHashSet();
            hosts.UnionWith(sources.Where(s => s.Item1 is not null).Select(s => s.Item1!));
            seen = c.Query<(string Repo, string? Tag, string Result)>("SELECT repo, tag, result FROM port_recipe_seen").ToDictionary(x => x.Repo, x => (x.Tag, x.Result));
            ports = c.Query<(long, string, string, string, string, string, string?)>("""
                SELECT p.id, p.console, p.game, p.project, p.link, COALESCE(k.name, p.console), p.repo FROM port p LEFT JOIN console k ON k.id = p.console
                WHERE p.game_id IS NOT NULL AND p.status <> 'no-port' AND p.delisted = 0 ORDER BY p.id
                """).ToList();
        }

        var todo = ports.Select(p => (p, src: Recipes.Source(p.Repo ?? p.Link))) // el repo que encontró Claude, si el enlace es una web (#155)
            .Where(x => x.src is { } s && (s.Source is "github" or "gitlab" || hosts.Contains(s.Host!)))
            .Select(x => (x.p, x.src!.Value.Source, x.src.Value.Repo, x.src.Value.Host, key: Key(x.src.Value.Host, x.src.Value.Repo)))
            .Where(x => !own.Contains(Ports.Key(x.p.Link)) && !ownRepos.Contains(x.key) && !(seen.TryGetValue(x.key, out var s) && s.Result != "noWindows"))
            .DistinctBy(x => x.key).ToList();

        var done = 0;
        foreach (var (p, source, repo, host, key) in todo)
        {
            run.Report(97, ApiError.Of("ports.recipesProgress", ("done", (done++).ToString(CultureInfo.InvariantCulture)), ("total", todo.Count.ToString(CultureInfo.InvariantCulture))));
            Recipes.Release? latest;
            try
            {
                var releases = await Recipes.Releases(source, host, repo, token, ct);
                latest = releases.FirstOrDefault(r => !r.Prerelease) ?? releases.FirstOrDefault();
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                run.Log(ApiError.Of("ports.log.recipesLimited"), "warn");
                break;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                run.Log(ApiError.Of("ports.log.recipeFailed", ("repo", repo), ("detail", e.Message)), "warn");
                continue;
            }
            var windows = latest?.Assets.Where(EmulatorCheck.IsWindows).ToList() ?? [];
            var linux = latest?.Assets.Where(EmulatorCheck.IsLinux).ToList() ?? [];
            if (windows.Count + linux.Count == 0)
            {
                Seen(db, key, latest?.Tag, "noWindows"); // shortcut: el nombre viene de antes de Linux; significa «sin archivos de PC»
                continue;
            }
            var data = new Data(p.Id, p.Game, p.Project, p.Console, p.Folder, source, host, repo, $"https://{host ?? "github.com"}/{repo}", latest!.Tag,
                latest.PublishedAt, windows, p.Link);
            if (await Create(db, data, windows, linux) is { } error)
            {
                run.Log(ApiError.Of("ports.log.recipeFailed", ("repo", repo), ("detail", error.Code)), "warn");
                continue;
            }
            Seen(db, key, latest.Tag, "accepted");
            created++;
        }
        run.Log(ApiError.Of("ports.log.recipes", ("created", created.ToString(CultureInfo.InvariantCulture))));
        return created;
    }

    /// <summary>
    /// El archivo de la receta: el único, o entre varios el de 64 bits que no sea de depuración ni símbolos. Null si sigue sin ser uno claro
    /// (la receta va sin él y con el aviso «Elegir el archivo»).
    /// </summary>
    internal static string? Pick(List<string> files)
    {
        if (files.Count == 1) return files[0];
        var real = files.Where(f => !Regex.IsMatch(f, "debug|symbols|pdb", RegexOptions.IgnoreCase)).ToList();
        if (real.Count == 1) return real[0];
        var x64 = real.Where(f => Regex.IsMatch(f, "x64|x86_64|amd64|win64", RegexOptions.IgnoreCase)).ToList();
        return x64.Count == 1 ? x64[0] : null;
    }

    /// <summary>
    /// Crea la receta con el archivo claro de cada sistema. Si de alguno hay varios sin uno claro, deja en su tarjeta el aviso «Elegir el
    /// archivo» con todos los de la release (otteretro-catalogo#153). Los archivos salieron de la release recién leída: no se vuelve a pedir.
    /// </summary>
    static async Task<ApiError?> Create(Database db, Data d, List<string> windows, List<string> linux)
    {
        var newId = NewId(db, d.Project);
        var (win, lin) = (Pick(windows), Pick(linux));
        if (await Recipes.Save(db, newId, Build(d, newId, win, lin).ToJsonString(), true, null, checkRelease: false) is { } error) return error;
        if (windows.Count > 0 && win is null || linux.Count > 0 && lin is null)
        {
            var notice = new JsonObject
            {
                ["recipe"] = newId, ["reason"] = "pickFile", ["repo"] = d.Repo, ["tag"] = d.Tag,
                ["files"] = new JsonArray([.. windows.Concat(linux).Select(f => JsonValue.Create(f))]),
            };
            using var c = db.Open();
            c.Execute("INSERT INTO review_item (kind, entity, data, created_at) VALUES ('doubtful', 'recipe', @data, @now)",
                new { data = notice.ToJsonString(), now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        }
        return null;
    }

    static void Seen(Database db, string repo, string? tag, string result)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO port_recipe_seen (repo, tag, result, checked_at) VALUES (@repo, @tag, @result, @now)
            ON CONFLICT (repo) DO UPDATE SET tag = excluded.tag, result = excluded.result, checked_at = excluded.checked_at
            """, new { repo, tag, result, now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
    }

    static Data Read(string json) => JsonSerializer.Deserialize<Data>(json, JsonSerializerOptions.Web)!;

    /// <summary>
    /// La receta mínima: lo que se puede sacar del port y su release. El ejecutable es un supuesto (el archivo si es un .exe, si no el
    /// nombre del repo), como en «Crear desde su repo»; lo demás (partidas, opciones, <c>detect</c> real) se completa en el editor.
    /// </summary>
    static JsonObject Build(Data d, string id, string? file, string? linux)
    {
        var updates = new JsonObject { ["source"] = d.Source };
        if (d.Host is not null) updates["host"] = d.Host;
        updates["repo"] = d.Repo;
        var exe = file is not null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : d.Repo.Split('/')[1] + ".exe";
        var r = new JsonObject
        {
            ["id"] = id,
            ["name"] = d.Project,
            ["kind"] = "port",
            ["folder"] = d.Folder,
            ["game"] = d.Game,
            ["port"] = d.Link ?? d.Url, // los candidatos de antes de #149 no guardaban el enlace; su repo suele serlo
            ["detect"] = exe,
            ["experimental"] = true,
            ["updates"] = updates,
        };
        if (file is not null) r["asset"] = Recipes.AssetPattern(file);
        if (linux is not null) r["assetLinux"] = Recipes.AssetPattern(linux);
        return r;
    }

    static string NewId(Database db, string project)
    {
        var slug = Regex.Replace(project.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "port";
        var id = slug;
        for (var n = 2; Recipes.Get(db, id) is not null; n++) id = $"{slug}-{n}";
        return id;
    }
}

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

/// <summary>Un candidato de receta para un port (#119), como lo muestra Revisar › Ports (#120).</summary>
/// <param name="Files">Los archivos de Windows de la release: con uno, la receta ya lo lleva; con varios, se elige uno al aceptar.</param>
/// <param name="Recipe">La receta que se crearía (con el único archivo, o sin <c>asset</c> hasta elegir uno).</param>
public record PortCandidate(long Id, long Port, string Game, string Project, string Console, string Url, string Tag, string? PublishedAt, List<string> Files,
    string Recipe);

/// <param name="File">El archivo de Windows elegido; hace falta si la release tiene varios.</param>
public record AcceptPortRequest(string? File = null);

/// <summary>
/// Recetas propuestas para los ports (#119, solicitud #118): un paso de la tarea «Ports», después del cruce y sin Claude. Cada port con
/// juego cuyo enlace es un repo de GitHub o Forgejo pide su última release; con archivos de Windows, va a Revisar una receta mínima
/// (<c>kind: port</c>, juego, carpeta de su consola, fuente y patrón del archivo). Lo visto queda en <c>port_recipe_seen</c>: lo
/// resuelto en Revisar no vuelve y lo que no tiene archivo de Windows se vuelve a mirar cuando cambie su release. Un repo que ya es
/// fuente de una receta no se propone.
/// </summary>
public static class PortRecipes
{
    /// <summary>Lo que espera en Revisar: el «N en Revisar» de la tarea «Ports».</summary>
    public static int Pending(Database db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<int>("SELECT count(*) FROM review_item WHERE entity = 'port' AND status = 'open'");
    }

    record Data(long Port, string Game, string Project, string Console, string Folder, string Source, string? Host, string Repo, string Url, string Tag,
        string? PublishedAt, List<string> Files, string? Link = null);

    static string Key(string? host, string repo) => $"{host ?? "github.com"}/{repo}".ToLowerInvariant();

    /// <summary>El paso de la tarea. Devuelve cuántos candidatos nuevos fueron a Revisar.</summary>
    public static async Task<int> Propose(TaskRun run, CancellationToken ct)
    {
        var db = run.Db;
        var token = run.Credentials.Read("github.token") is { Length: > 0 } t ? t : null;
        HashSet<string> own, ownRepos; // los ports que ya tienen receta: por su enlace (#149) o, en una receta sin «port», por su repo
        // ponytail: Forgejo solo en Codeberg y los servidores de las recetas; cualquier otro enlace (una web, GitLab) no es un repo con esta API.
        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase) { "codeberg.org" };
        Dictionary<string, (string? Tag, string Result)> seen;
        List<(long Id, string Console, string Game, string Project, string Link, string Folder)> ports;
        using (var c = db.Open())
        {
            var recipes = c.Query<string>("SELECT json FROM recipe").Select(j => JsonNode.Parse(j)!).ToList();
            own = recipes.Select(r => (string?)r["port"]).OfType<string>().Select(Ports.Key).ToHashSet();
            var sources = recipes.Select(r => r["updates"])
                .Where(u => (string?)u?["repo"] is not null).Select(u => ((string?)u!["host"], (string)u["repo"]!)).ToList();
            ownRepos = sources.Select(s => Key(s.Item1, s.Item2)).ToHashSet();
            hosts.UnionWith(sources.Where(s => s.Item1 is not null).Select(s => s.Item1!));
            seen = c.Query<(string Repo, string? Tag, string Result)>("SELECT repo, tag, result FROM port_recipe_seen").ToDictionary(x => x.Repo, x => (x.Tag, x.Result));
            ports = c.Query<(long, string, string, string, string, string)>("""
                SELECT p.id, p.console, p.game, p.project, p.link, COALESCE(k.name, p.console) FROM port p LEFT JOIN console k ON k.id = p.console
                WHERE p.game_id IS NOT NULL AND p.status <> 'no-port' AND p.delisted = 0 ORDER BY p.id
                """).ToList();
        }

        var todo = ports.Select(p => (p, src: Recipes.Source(p.Link)))
            .Where(x => x.src is { } s && (s.Source == "github" || hosts.Contains(s.Host!)))
            .Select(x => (x.p, x.src!.Value.Source, x.src.Value.Repo, x.src.Value.Host, key: Key(x.src.Value.Host, x.src.Value.Repo)))
            .Where(x => !own.Contains(Ports.Key(x.p.Link)) && !ownRepos.Contains(x.key) && !(seen.TryGetValue(x.key, out var s) && s.Result != "noWindows"))
            .DistinctBy(x => x.key).ToList();

        var added = 0;
        foreach (var (p, source, repo, host, key) in todo)
        {
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
            var files = latest?.Assets.Where(EmulatorCheck.IsWindows).ToList() ?? [];
            if (files.Count == 0)
            {
                Seen(db, key, latest?.Tag, "noWindows");
                continue;
            }
            var data = new Data(p.Id, p.Game, p.Project, p.Console, p.Folder, source, host, repo, $"https://{host ?? "github.com"}/{repo}", latest!.Tag,
                latest.PublishedAt, files, p.Link);
            using (var c = db.Open())
                c.Execute("INSERT INTO review_item (kind, entity, entity_id, data, created_at) VALUES ('doubtful', 'port', @Port, @json, @now)",
                    new { data.Port, json = JsonSerializer.Serialize(data, JsonSerializerOptions.Web), now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
            Seen(db, key, latest.Tag, "review");
            added++;
        }
        run.Log(ApiError.Of("ports.log.recipes", ("added", added.ToString(CultureInfo.InvariantCulture))));
        return added;
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
    static JsonObject Build(Data d, string id, string? file)
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

    public static List<PortCandidate> List(Database db)
    {
        using var c = db.Open();
        return c.Query<(long Id, string Data)>("SELECT id, data FROM review_item WHERE entity = 'port' AND status = 'open' ORDER BY id").Select(r =>
        {
            var d = Read(r.Data);
            var recipe = Build(d, NewId(db, d.Project), d.Files.Count == 1 ? d.Files[0] : null).ToJsonString(Recipes.Indented).ReplaceLineEndings("\n");
            return new PortCandidate(r.Id, d.Port, d.Game, d.Project, d.Console, d.Url, d.Tag, d.PublishedAt, d.Files, recipe);
        }).ToList();
    }

    /// <summary>
    /// Acepta un candidato: crea la receta experimental con el archivo elegido (o el único) y la guarda como cualquier receta (esquema y
    /// una release que calce con el patrón). Found es false si el candidato no existe o ya se resolvió.
    /// </summary>
    public static async Task<(bool Found, ApiError? Error)> Accept(Database db, long id, string? file, string? token, CancellationToken ct = default)
    {
        string? json;
        using (var c = db.Open())
            json = c.ExecuteScalar<string?>("SELECT data FROM review_item WHERE id = @id AND entity = 'port' AND status = 'open'", new { id });
        if (json is null) return (false, null);
        var d = Read(json);
        file ??= d.Files.Count == 1 ? d.Files[0] : null;
        if (file is null || !d.Files.Contains(file)) return (true, ApiError.Of("review.chooseFile"));
        var newId = NewId(db, d.Project);
        if (await Recipes.Save(db, newId, Build(d, newId, file).ToJsonString(), true, token, ct) is { } error) return (true, error);
        Resolve(db, id, d, "approved", "accepted");
        return (true, null);
    }

    /// <summary>Descarta un candidato: queda visto y no vuelve a salir. False si no existe o ya se resolvió.</summary>
    public static bool Dismiss(Database db, long id)
    {
        string? json;
        using (var c = db.Open())
            json = c.ExecuteScalar<string?>("SELECT data FROM review_item WHERE id = @id AND entity = 'port' AND status = 'open'", new { id });
        if (json is null) return false;
        Resolve(db, id, Read(json), "rejected", "rejected");
        return true;
    }

    static void Resolve(Database db, long id, Data d, string status, string result)
    {
        using (var c = db.Open()) c.Execute("UPDATE review_item SET status = @status WHERE id = @id", new { id, status });
        Seen(db, Key(d.Host, d.Repo), d.Tag, result);
    }
}

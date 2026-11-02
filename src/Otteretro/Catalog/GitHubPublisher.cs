using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="Kind">saga o game.</param>
public record PublishRequest(string Kind, long Id);
/// <param name="Change">added, modified o deleted.</param>
/// <param name="Details">Qué cambia, como códigos que traduce el front (publish.change.*).</param>
public record PublishChange(string Path, string Change, List<ApiError> Details);
/// <param name="Errors">Lo que la validación del catálogo rechaza: con algo acá no se publica.</param>
/// <param name="Conflicts">Archivos que cambiaron en main después de la versión de la que parte el catálogo local.</param>
/// <param name="KnownBase">Se sabe de qué versión de main parte el catálogo local; si no, los conflictos no se pueden detectar.</param>
/// <param name="OpenPr">El PR que ya está abierto para esto: publicar otra vez lo actualiza.</param>
public record PublishPreview(string Title, List<PublishChange> Files, List<string> Errors, List<string> Conflicts, bool KnownBase, string? OpenPr);
public record PublishResult(string Url, bool Updated);

/// <summary>
/// Publicar tu versión de una saga o de un juego como un PR (#22). Con permiso de escritura en el repo, la rama va ahí; si no, en un
/// fork de la cuenta de la persona. Solo propone el cambio: este programa no tiene ninguna forma de fusionar un PR.
/// </summary>
public sealed class GitHubPublisher(Database db, string token, HttpMessageHandler? handler = null)
{
    public const string Repo = "OscarVargas97/otteretro-catalogo";
    /// <summary>De qué commit de main parte el catálogo local (setting): con él se ve si alguien cambió lo mismo después.</summary>
    public const string BaseSetting = "catalog.base";
    const string Folder = "catalogo";

    /// <summary>Lo único que se pide a la API de GitHub; nada de esto fusiona.</summary>
    readonly HttpClient http = new(handler ?? new HttpClientHandler()) { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };

    HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("OtteretroCatalogo (+https://github.com/" + Repo + ")");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>La respuesta como JSON, o null si es un 404. Los errores nunca llevan la clave: solo el código y la ruta.</summary>
    async Task<JsonNode?> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(method, path, body), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new TaskException(ApiError.Of("publish.badToken"));
        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write(LogLevel.Warning, "GitHub", $"{method} {path}: {(int)response.StatusCode}");
            throw new TaskException(ApiError.Of(response.StatusCode == HttpStatusCode.Forbidden ? "publish.forbidden" : "publish.failed", ("status", ((int)response.StatusCode).ToString())));
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    Task<JsonNode?> Get(string path, CancellationToken ct) => Send(HttpMethod.Get, path, null, ct);

    async Task<JsonNode> Must(HttpMethod method, string path, object? body, CancellationToken ct) =>
        await Send(method, path, body, ct) ?? throw new TaskException(ApiError.Of("publish.failed", ("status", "404")));

    sealed record Plan(string Head, string Title, string Branch, List<(PublishChange Change, string? LocalFile)> Files, List<string> Errors, List<string> Conflicts, bool KnownBase);

    /// <summary>Qué cambiaría el PR: lo publicado de lo elegido, comparado con main, y si el resultado pasa la validación.</summary>
    public async Task<PublishPreview> Preview(PublishRequest request, CancellationToken ct = default)
    {
        var plan = await MakePlan(request, ct);
        var (owner, _) = await Target(createFork: false, ct);
        var open = owner is null ? null : await OpenPr(owner, plan.Branch, ct);
        return new(plan.Title, plan.Files.Select(f => f.Change).ToList(), plan.Errors, plan.Conflicts, plan.KnownBase, open);
    }

    /// <summary>Crea la rama con lo elegido y abre el PR, o actualiza el que ya estaba abierto.</summary>
    public async Task<PublishResult> Publish(PublishRequest request, CancellationToken ct = default)
    {
        var plan = await MakePlan(request, ct);
        if (plan.Files.Count == 0) throw new TaskException(ApiError.Of("publish.nothingChanged"));
        if (plan.Errors.Count > 0) throw new TaskException(ApiError.Of("publish.invalid", ("count", plan.Errors.Count.ToString())));
        if (plan.Conflicts.Count > 0) throw new TaskException(ApiError.Of("publish.conflict", ("files", string.Join(", ", plan.Conflicts))));

        var (owner, target) = await Target(createFork: true, ct);
        var baseTree = (await Must(HttpMethod.Get, $"repos/{Repo}/git/commits/{plan.Head}", null, ct))["tree"]!["sha"]!.GetValue<string>();
        var tree = new List<object>();
        foreach (var (change, local) in plan.Files)
        {
            var path = $"{Folder}/{change.Path}";
            if (local is null) tree.Add(new { path, mode = "100644", type = "blob", sha = (string?)null });
            else
            {
                var blob = await Must(HttpMethod.Post, $"repos/{target}/git/blobs", new { content = await File.ReadAllTextAsync(local, ct), encoding = "utf-8" }, ct);
                tree.Add(new { path, mode = "100644", type = "blob", sha = blob["sha"]!.GetValue<string>() });
            }
        }
        var newTree = await Must(HttpMethod.Post, $"repos/{target}/git/trees", new { base_tree = baseTree, tree }, ct);
        var commit = await Must(HttpMethod.Post, $"repos/{target}/git/commits",
            new { message = $"feat(catalogo): {plan.Title}", tree = newTree["sha"]!.GetValue<string>(), parents = new[] { plan.Head } }, ct);
        var sha = commit["sha"]!.GetValue<string>();
        // Publicar otra vez lo mismo mueve la rama al commit nuevo: el PR abierto se actualiza solo.
        if (await Get($"repos/{target}/git/ref/heads/{plan.Branch}", ct) is null)
            await Must(HttpMethod.Post, $"repos/{target}/git/refs", new { @ref = $"refs/heads/{plan.Branch}", sha }, ct);
        else
            await Must(HttpMethod.Patch, $"repos/{target}/git/refs/heads/{plan.Branch}", new { sha, force = true }, ct);

        if (await OpenPr(owner!, plan.Branch, ct) is { } url) return new(url, true);
        var pr = await Must(HttpMethod.Post, $"repos/{Repo}/pulls", new
        {
            title = $"Catálogo: {plan.Title}",
            head = $"{owner}:{plan.Branch}",
            @base = "main",
            body = Body(plan),
            maintainer_can_modify = true,
        }, ct);
        return new(pr["html_url"]!.GetValue<string>(), false);
    }

    static string Body(Plan plan) =>
        $"Publicado desde Otteretro Catálogo.\n\n{string.Join("\n", plan.Files.Select(f => $"- `{Folder}/{f.Change.Path}`: {f.Change.Change}"))}\n\nDatos de juegos: [IGDB.com](https://www.igdb.com).";

    async Task<string?> OpenPr(string owner, string branch, CancellationToken ct) =>
        (await Get($"repos/{Repo}/pulls?state=open&head={owner}:{Uri.EscapeDataString(branch)}", ct))?.AsArray().FirstOrDefault()?["html_url"]?.GetValue<string>();

    /// <summary>Dónde va la rama: el repo, si la cuenta puede escribir en él, o su fork (que se crea y se pone al día con main).</summary>
    async Task<(string? Owner, string Target)> Target(bool createFork, CancellationToken ct)
    {
        var login = (await Must(HttpMethod.Get, "user", null, ct))["login"]!.GetValue<string>();
        var repo = await Must(HttpMethod.Get, $"repos/{Repo}", null, ct);
        if (repo["permissions"]?["push"]?.GetValue<bool>() == true) return (Repo.Split('/')[0], Repo);
        var fork = $"{login}/{Repo.Split('/')[1]}";
        if (!createFork) return (await Get($"repos/{fork}", ct) is null ? null : login, fork);
        if (await Get($"repos/{fork}", ct) is null)
        {
            await Must(HttpMethod.Post, $"repos/{Repo}/forks", new { default_branch_only = true }, ct);
            // GitHub crea el fork en segundo plano.
            for (var i = 0; i < 30 && await Get($"repos/{fork}", ct) is null; i++) await Task.Delay(Poll, ct);
        }
        await Send(HttpMethod.Post, $"repos/{fork}/merge-upstream", new { branch = "main" }, ct);
        return (login, fork);
    }

    /// <summary>Cada cuánto se pregunta si el fork ya existe; las pruebas lo acortan.</summary>
    public static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(2);

    async Task<Plan> MakePlan(PublishRequest request, CancellationToken ct)
    {
        if (request.Kind is not ("saga" or "game")) throw new TaskException(ApiError.Of("publish.kind"));
        using var c = db.Open();
        var title = request.Kind == "saga"
            ? c.ExecuteScalar<string?>("SELECT name FROM saga WHERE id = @Id", request)
            : c.ExecuteScalar<string?>("SELECT name_es FROM game WHERE id = @Id", request)
              ?? c.ExecuteScalar<string?>("SELECT 'juego ' || old_id FROM replaced WHERE kind = 'game' AND old_id = @Id", request);
        if (title is null) throw new TaskException(ApiError.Of(request.Kind == "saga" ? "edit.noSaga" : "edit.noGame", ("id", request.Id.ToString())));

        var head = (await Must(HttpMethod.Get, $"repos/{Repo}/git/ref/heads/main", null, ct))["object"]!["sha"]!.GetValue<string>();
        var main = await MainFolder(head, ct);
        var local = Path.Combine(Work, "local");
        if (Directory.Exists(local)) Directory.Delete(local, true);
        Published.Write(db, local);

        var paths = Affected(request, main, local);
        var files = new List<(PublishChange, string?)>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            var (before, after) = (Path.Combine(main, path), Path.Combine(local, path));
            var (had, has) = (File.Exists(before), File.Exists(after));
            if (had && has && File.ReadAllText(before) == File.ReadAllText(after)) continue;
            if (!had && !has) continue;
            files.Add((new PublishChange(path, !had ? "added" : !has ? "deleted" : "modified", Details(path, had ? before : null, has ? after : null)), has ? after : null));
        }

        // Lo que quedaría en main con el PR: tiene que pasar la misma validación que corre en el PR.
        var result = Path.Combine(Work, "resultado");
        if (Directory.Exists(result)) Directory.Delete(result, true);
        Copy(main, result);
        foreach (var (change, file) in files)
        {
            var target = Path.Combine(result, change.Path);
            if (file is null) File.Delete(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
        var errors = files.Count == 0 ? [] : Published.Validate(result);

        var baseSha = c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @BaseSetting", new { BaseSetting });
        var conflicts = new List<string>();
        if (baseSha is not null && baseSha != head && files.Count > 0)
        {
            var changed = (await Must(HttpMethod.Get, $"repos/{Repo}/compare/{baseSha}...{head}", null, ct))["files"]?.AsArray()
                .Select(f => f!["filename"]!.GetValue<string>()).ToHashSet() ?? [];
            conflicts = files.Select(f => $"{Folder}/{f.Item1.Path}").Where(changed.Contains).ToList();
        }
        return new(head, title, $"catalogo/{request.Kind}-{request.Id}", files, errors, conflicts, baseSha is not null);
    }

    /// <summary>
    /// Los archivos que toca publicar lo elegido: el de la saga (y el de antes, si cambió su nombre) y cualquier archivo que tenga, en main o
    /// acá, uno de sus juegos (de donde salió un juego movido, por ejemplo). Más `reemplazados.json` si cambió.
    /// </summary>
    static HashSet<string> Affected(PublishRequest request, string main, string local)
    {
        var (mainGames, localGames) = (Published.GameFiles(main), Published.GameFiles(local));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var games = new HashSet<long>();
        if (request.Kind == "saga")
            foreach (var folder in new[] { main, local })
                foreach (var file in Published.SagaFiles(folder, request.Id))
                {
                    paths.Add(file);
                    games.UnionWith(Published.GamesIn(Path.Combine(folder, file)));
                }
        else games.Add(request.Id);
        foreach (var g in games)
        {
            if (mainGames.TryGetValue(g, out var m)) paths.Add(m);
            if (localGames.TryGetValue(g, out var l)) paths.Add(l);
        }
        paths.Add(Published.ReplacedPath);
        return paths;
    }

    static List<ApiError> Details(string path, string? before, string? after) =>
        Published.Describe(path, before is null ? null : File.ReadAllText(before), after is null ? null : File.ReadAllText(after));

    /// <summary>Dónde se baja main y se arma lo publicado; las pruebas usan su propia carpeta.</summary>
    public string Work { get; init; } = Path.Combine(Path.GetTempPath(), "otteretro-publicar");

    /// <summary>La carpeta `catalogo` de main en ese commit; se baja una vez por commit.</summary>
    async Task<string> MainFolder(string head, CancellationToken ct)
    {
        var folder = Path.Combine(Work, "main-" + head);
        if (Directory.Exists(folder)) return folder;
        using var response = await http.SendAsync(Request(HttpMethod.Get, $"repos/{Repo}/zipball/{head}"), ct);
        if (!response.IsSuccessStatusCode) throw new TaskException(ApiError.Of("publish.failed", ("status", ((int)response.StatusCode).ToString())));
        var partial = folder + ".part";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        using (var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(ct)))
            foreach (var entry in zip.Entries)
            {
                // El zip trae todo dentro de una carpeta «dueño-repo-sha»; solo interesa lo publicado.
                var parts = entry.FullName.Split('/', 3);
                if (parts.Length < 3 || parts[1] != Folder || entry.FullName.EndsWith('/') || parts[2].Contains("..")) continue;
                var target = Path.Combine(partial, parts[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
        Directory.Move(partial, folder);
        return folder;
    }

    static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>`catalogo base &lt;sha&gt;`: de qué commit de main parte el catálogo local.</summary>
    public static void SetBase(Database db, string sha)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO setting (name, value, machine_id) VALUES (@BaseSetting, @sha, '') ON CONFLICT (name, machine_id) DO UPDATE SET value = excluded.value",
            new { BaseSetting, sha });
    }
}

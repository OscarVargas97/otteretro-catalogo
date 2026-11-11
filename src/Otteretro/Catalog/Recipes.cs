using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>Una receta en la lista de «Emuladores» (#95).</summary>
/// <param name="Consoles">Las que corre; un port, la de su fila del catálogo de ports (#149).</param>
/// <param name="Game">Port: su juego.</param>
/// <param name="Notices">Sus avisos de las tareas (otteretro-catalogo#153): un patrón que ya no calza, un repo que cambió o un archivo por elegir.</param>
public record RecipeInfo(string Id, string Name, string Kind, string Folder, List<string> Consoles, bool Local = false, string? Url = null, string? ForkOf = null,
    string? Game = null, List<RecipeNotice>? Notices = null);
/// <summary>Una receta que recomienda un juego (#123), con la fuente de la recomendación y las consolas que corre (#132).</summary>
public record GameRecipe(string Id, string Name, string Kind, string? Source, List<string> Consoles);
/// <summary>El recomendado de un juego en una consola, elegido a mano en su ficha (#132); la fuente es opcional.</summary>
public record RecommendRequest(string Recipe, string? Source = null);
/// <summary>Una receta entera, como JSON.</summary>
public record RecipeText(string Json);
/// <summary>
/// Lo que muestra la tarjeta de una receta (#108, decisión 365): su última release (sin borradores) y su fecha, si el patrón calza con
/// algún archivo de ella, y si está publicada (igual que en main), cambiada sin publicar o es local (de Otteretro, todavía sin traer).
/// Release y calce van null si la receta no tiene fuente con releases o no se pudo consultar; State, si no se pudo leer main.
/// </summary>
public record RecipeStatus(string Id, string? Tag, string? PublishedAt, bool? Matches, string? State);
/// <summary>
/// El resultado de «Probar la descarga» (#109): la release, el archivo que bajó y su tamaño, y si adentro está el ejecutable de la receta
/// (<c>detect</c>). Found es null si no se puede mirar adentro (un .7z, por ejemplo).
/// </summary>
public record RecipeTest(string Tag, string File, long Size, string Detect, bool? Found);
/// <summary>La URL del repo de un emulador (GitHub, Forgejo o Gitea).</summary>
public record RepoRequest(string Url, string? File = null);
/// <summary>Un archivo de una release (#110): su nombre, si parece de Windows y el patrón que saldría de él.</summary>
public record RepoFile(string Name, bool Windows, string Pattern);
/// <summary>Una release de un repo con sus archivos (#110).</summary>
public record RepoRelease(string Tag, string? PublishedAt, List<RepoFile> Files);

/// <summary>
/// La sección «Emuladores» (#95, decisiones 352 y 353 de Otteretro). Las recetas se guardan en la base local (`catalog.recipe`), como las
/// sagas: `Published.Write` las escribe en `recetas/&lt;id&gt;.json` y Publicar lleva al PR las que cambiaron. Se guarda solo una receta que
/// cumple el esquema y cuya fuente tiene una release con un archivo que calza con su patrón (`asset`).
/// </summary>
public static partial class Recipes
{
    /// <summary>Para pedir las releases; las pruebas lo cambian por uno falso.</summary>
    public static HttpClient Http
    {
        get => http;
        set { http = value; Latest.Clear(); }
    }
    static HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>La última release de cada fuente (#108), por un rato: la lista de tarjetas no consulta cada repo cada vez que se abre.</summary>
    // ponytail: en memoria y por tiempo; se pierde al cerrar el programa, y una release nueva tarda hasta LatestFor en verse.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, Release? Release)> Latest = new();
    public static TimeSpan LatestFor { get; set; } = TimeSpan.FromMinutes(15);

    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static RecipeInfo Info(string json, bool local, Database db, ILookup<string, RecipeNotice> notices)
    {
        var r = JsonNode.Parse(json)!;
        var port = (string?)r["kind"] == "port" && (string?)r["port"] is { } link ? Ports.ByLink(db, link) : null;
        return new RecipeInfo((string)r["id"]!, (string?)r["name"] ?? "", (string?)r["kind"] ?? "", (string?)r["folder"] ?? "",
            port is null ? Consoles(r) : [port.Console], local, RepoUrl(r), (string?)r["forkOf"], port?.Game ?? (string?)r["game"], [.. notices[(string)r["id"]!]]);
    }

    internal static string? RepoUrl(JsonNode r) => (string?)r["updates"]?["repo"] is { } repo
        ? (string?)r["updates"]?["source"] switch { "github" => "https://github.com/" + repo, "forgejo" or "gitlab" when (string?)r["updates"]?["host"] is { } h => $"https://{h}/{repo}", _ => null }
        : null;

    public static List<RecipeInfo> List(Database db, string? local = null)
    {
        var notices = EmulatorReview.Notices(db).ToLookup(n => n.Recipe);
        using var c = db.Open();
        return c.Query<string>("SELECT json FROM recipe ORDER BY id").Select(json => Info(json, false, db, notices))
            .Concat(Local(db, local).Values.Select(json => Info(json, true, db, notices))).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Las recetas que recomiendan un juego del catálogo (#123): las que lo traen en <c>recommendedFor</c> con su <c>gameId</c>.</summary>
    public static List<GameRecipe> ForGame(Database db, long gameId)
    {
        using var c = db.Open();
        return c.Query<string>("SELECT json FROM recipe ORDER BY id").SelectMany(json =>
        {
            var r = JsonNode.Parse(json)!;
            return (r["recommendedFor"]?.AsArray() ?? []).Where(g => (long?)g?["gameId"] == gameId)
                .Select(g => new GameRecipe((string)r["id"]!, (string?)r["name"] ?? "", (string?)r["kind"] ?? "", (string?)g!["source"], Consoles(r)));
        }).ToList();
    }

    static List<string> Consoles(JsonNode r) => r["consoles"]?.AsArray().Select(x => (string)x!).ToList() ?? [];

    /// <summary>
    /// Elige a mano el recomendado de un juego en una consola (#132, plan de #128): suma <c>{ game, gameId, source }</c> al
    /// <c>recommendedFor</c> de <paramref name="recipeId"/> y lo saca de las otras recetas de esa consola, para que quede uno solo. Sin
    /// receta, solo lo quita. Se guarda con <see cref="Save"/>, como un cambio sin publicar.
    /// </summary>
    public static async Task<ApiError?> Recommend(Database db, long gameId, string console, string? recipeId, string? source, string? token, CancellationToken ct = default)
    {
        string? name;
        List<JsonNode> all;
        using (var c = db.Open())
        {
            name = c.ExecuteScalar<string?>("SELECT name_es FROM game WHERE id = @gameId", new { gameId });
            all = c.Query<string>("SELECT json FROM recipe ORDER BY id").Select(json => JsonNode.Parse(json)!).ToList();
        }
        if (name is null) return ApiError.Of("recipes.noGame");
        var chosen = recipeId is null ? null : all.FirstOrDefault(r => (string?)r["id"] == recipeId);
        if (recipeId is not null && (chosen is null || !Consoles(chosen).Contains(console))) return ApiError.Of("recipes.notForConsole", ("id", recipeId), ("console", console));

        bool Drop(JsonNode r)
        {
            if (r["recommendedFor"] is not JsonArray games) return false;
            var removed = games.Where(g => (long?)g?["gameId"] == gameId).ToList();
            foreach (var g in removed) games.Remove(g);
            if (games.Count == 0) r.AsObject().Remove("recommendedFor");
            return removed.Count > 0;
        }
        // La elegida primero: si no se puede guardar, el juego sigue con la que tenía.
        if (chosen is not null)
        {
            Drop(chosen);
            var item = new JsonObject { ["game"] = name, ["gameId"] = gameId };
            if (source?.Trim() is { Length: > 0 } s) item["source"] = s;
            if (chosen["recommendedFor"] is JsonArray games) games.Add(item); else chosen["recommendedFor"] = new JsonArray(item);
            if (await Save(db, recipeId!, chosen.ToJsonString(), false, token, ct) is { } error) return error;
        }
        foreach (var r in all.Where(r => r != chosen && Consoles(r).Contains(console) && Drop(r)))
            if (await Save(db, (string)r["id"]!, r.ToJsonString(), false, token, ct) is { } error) return error;
        return null;
    }

    public static string? Get(Database db, string id)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT json FROM recipe WHERE id = @id", new { id }) ?? Local(db).GetValueOrDefault(id);
    }

    /// <summary>La carpeta de recetas de Otteretro del mismo equipo (`data\recetas` de su carpeta de datos), o null si no está instalado.</summary>
    public static string? LocalFolder() => ImageHome.OtteretroDataFolder() is { } d ? Path.Combine(d, "data", "recetas") : null;

    /// <summary>
    /// Las recetas de Otteretro (#96, decisión 350) que la base local todavía no tiene, por id y con su texto: solo lectura, nunca se
    /// escribe en esa carpeta. Se ignoran `_respaldos`, los esquemas y lo que no cumple el esquema.
    /// </summary>
    public static Dictionary<string, string> Local(Database db, string? folder = null)
    {
        var found = new Dictionary<string, string>();
        folder ??= LocalFolder();
        if (folder is null || !Directory.Exists(folder)) return found;
        using var c = db.Open();
        foreach (var file in Directory.GetFiles(folder, "*.json").Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            try
            {
                var text = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(text);
                if (Published.CheckRecipe(doc.RootElement).Count > 0) continue;
                var id = doc.RootElement.GetProperty("id").GetString()!;
                if (c.ExecuteScalar<long>("SELECT count(*) FROM recipe WHERE id = @id", new { id }) == 0) found[id] = text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { }
        }
        return found;
    }

    /// <summary>Trae las recetas de una carpeta (la de main) que todavía no están en la base, con sus bytes. Devuelve cuántas trajo.</summary>
    public static int Import(Database db, string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        using var c = db.Open();
        var count = 0;
        foreach (var file in Directory.GetFiles(folder, "*.json").Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(text);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            if (c.ExecuteScalar<long>("SELECT count(*) FROM recipe WHERE id = @id", new { id }) > 0) continue;
            Store(c, doc.RootElement, text);
            count++;
        }
        return count;
    }

    internal static void Store(System.Data.IDbConnection c, JsonElement r, string text)
    {
        var id = r.GetProperty("id").GetString()!;
        c.Execute("INSERT INTO recipe (id, kind, json) VALUES (@id, @kind, @text) ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, json = excluded.json",
            new { id, kind = r.GetProperty("kind").GetString(), text });
        c.Execute("DELETE FROM recipe_console WHERE recipe_id = @id", new { id });
        if (r.TryGetProperty("consoles", out var consoles))
            foreach (var console in consoles.EnumerateArray().Select(x => x.GetString()!).Distinct())
                c.Execute("INSERT INTO recipe_console (recipe_id, console_id) VALUES (@id, @console)", new { id, console });
    }

    /// <summary>
    /// Guarda la receta <paramref name="id"/> (nueva, con <paramref name="create"/>) si cumple el esquema y su fuente tiene una release con
    /// un archivo que calce con su patrón; si no, el porqué y no guarda nada.
    /// </summary>
    public static async Task<ApiError?> Save(Database db, string id, string json, bool create, string? token, CancellationToken ct = default, bool checkRelease = true)
    {
        if (FromPort(db, json) is { } port)
        {
            if (port.Error is not null) return port.Error;
            json = port.Json!;
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { return ApiError.Of("recipes.badJson", ("detail", e.Message)); }
        using (doc)
        {
            var r = doc.RootElement;
            if (Published.CheckRecipe(r) is { Count: > 0 } problems) return ApiError.Of("recipes.schema", ("problems", string.Join("; ", problems)));
            if (r.GetProperty("id").GetString() != id) return ApiError.Of("recipes.idChanged", ("id", id));
            var before = Get(db, id);
            if (create && before is not null) return ApiError.Of("recipes.exists", ("id", id));
            if (checkRelease && await CheckRelease(r, token, ct) is { } error) return error;
            // Sin cambios, conserva sus bytes (los de main): así no aparece como cambiada solo por el formato.
            bool same;
            using (var old = before is null ? null : JsonDocument.Parse(before)) same = old is not null && JsonElement.DeepEquals(old.RootElement, r);
            using var c = db.Open();
            Store(c, r, same ? before! : JsonSerializer.Serialize(r, Indented).ReplaceLineEndings("\n") + "\n");
            return null;
        }
    }

    /// <summary>
    /// Una receta de port (#149) apunta a su fila del catálogo de ports con <c>port</c> (su enlace) y de ahí toma su juego y su carpeta (el
    /// nombre de su consola): no se escriben a mano. Null si no es un port o no es JSON (eso lo dice Save).
    /// </summary>
    static (string? Json, ApiError? Error)? FromPort(Database db, string json)
    {
        JsonObject r;
        try { if (JsonNode.Parse(json) is JsonObject o && (string?)o["kind"] == "port") r = o; else return null; }
        catch (JsonException) { return null; }
        if ((string?)r["port"] is not { Length: > 0 } link) return (null, ApiError.Of("recipes.noPort"));
        if (Ports.ByLink(db, link) is not { } port) return (null, ApiError.Of("recipes.portUnknown", ("link", link)));
        r["folder"] = port.Folder;
        r["game"] = port.Game;
        return (r.ToJsonString(), null);
    }

    /// <summary>
    /// Que la fuente de la receta tenga una release con un archivo que calce con <c>asset</c>. Sin <c>asset</c> o sin <c>updates</c> no hay
    /// qué revisar; <c>dolphin</c> publica en su propio servidor, sin releases.
    /// </summary>
    static async Task<ApiError?> CheckRelease(JsonElement r, string? token, CancellationToken ct)
    {
        if (!r.TryGetProperty("asset", out var asset) || !r.TryGetProperty("updates", out var u)) return null;
        var source = u.GetProperty("source").GetString()!;
        if (source is not ("github" or "forgejo" or "gitlab")) return null;
        var repo = u.TryGetProperty("repo", out var x) ? x.GetString() : null;
        var host = u.TryGetProperty("host", out var h) ? h.GetString() : null;
        if (repo is null || !HasRepo(source, host)) return ApiError.Of("recipes.noRepo");
        var pattern = asset.GetString()!;
        try
        {
            var releases = await Releases(source, host, repo, token, ct);
            if (releases.Count == 0) return ApiError.Of("recipes.noRelease", ("repo", repo));
            return releases.Any(rel => rel.Assets.Any(name => Regex.IsMatch(name, pattern))) ? null : ApiError.Of("recipes.noAsset", ("repo", repo), ("pattern", pattern));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ApiError.Of("recipes.sourceFailed", ("repo", repo), ("detail", e.Message));
        }
    }

    /// <summary>
    /// El estado de cada receta para su tarjeta (#108). <paramref name="main"/> es la carpeta de recetas de main (null si no se pudo
    /// traer: el estado queda sin saber). Las releases se piden en paralelo y se guardan un rato; una fuente que falla queda sin release.
    /// </summary>
    public static async Task<List<RecipeStatus>> Status(Database db, string? main, string? token, string? local = null, CancellationToken ct = default)
    {
        List<(string Id, string Json, bool Local)> all;
        using (var c = db.Open())
            all = c.Query<(string, string)>("SELECT id, json FROM recipe ORDER BY id").Select(r => (r.Item1, r.Item2, false))
                .Concat(Local(db, local).Select(r => (r.Key, r.Value, true))).ToList();
        return (await Task.WhenAll(all.Select(async r =>
        {
            var node = JsonNode.Parse(r.Json)!;
            var (tag, at, matches) = await LatestRelease(node, token, ct);
            var file = main is null ? null : Path.Combine(main, r.Id + ".json");
            var state = r.Local ? "local" : file is null ? null : File.Exists(file) && SameJson(File.ReadAllText(file), r.Json) ? "published" : "changed";
            return new RecipeStatus(r.Id, tag, at, matches, state);
        }))).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    static bool SameJson(string a, string b)
    {
        try
        {
            using var x = JsonDocument.Parse(a);
            using var y = JsonDocument.Parse(b);
            return JsonElement.DeepEquals(x.RootElement, y.RootElement);
        }
        catch (JsonException) { return false; }
    }

    static async Task<(string? Tag, string? At, bool? Matches)> LatestRelease(JsonNode r, string? token, CancellationToken ct)
    {
        var source = (string?)r["updates"]?["source"];
        var repo = (string?)r["updates"]?["repo"];
        var host = (string?)r["updates"]?["host"];
        if (repo is null || !HasRepo(source, host)) return (null, null, null);
        var key = $"{source}|{host}|{repo}";
        Release? latest;
        if (Latest.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < LatestFor) latest = hit.Release;
        else
        {
            try { latest = (await Releases(source, host, repo, token, ct)).FirstOrDefault(); }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { return (null, null, null); }
            Latest[key] = (DateTime.UtcNow, latest);
        }
        if (latest is null) return (null, null, null);
        var pattern = (string?)r["asset"];
        return (latest.Tag, latest.PublishedAt, pattern is null ? null : latest.Assets.Any(a => Regex.IsMatch(a, pattern)));
    }

    internal sealed record Release(string Tag, string? PublishedAt, List<string> Assets, Dictionary<string, string>? Urls = null, bool Prerelease = false);

    /// <summary>
    /// Descarta lo no publicado (#109): la receta vuelve a la de main (<paramref name="main"/>) o, si main no la tiene, se borra de la base
    /// local. Devuelve false si no se pudo leer main (no hay con qué comparar).
    /// </summary>
    public static bool Discard(Database db, string id, string? main)
    {
        if (main is null) return false;
        var file = Path.Combine(main, Path.GetFileName(id) + ".json");
        using var c = db.Open();
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(text);
            Store(c, doc.RootElement, text);
        }
        else
        {
            c.Execute("DELETE FROM recipe_console WHERE recipe_id = @id", new { id });
            c.Execute("DELETE FROM recipe WHERE id = @id", new { id });
        }
        return true;
    }

    /// <summary>
    /// «Probar la descarga» (#109): baja el archivo de la última release que calza con el patrón a una carpeta temporal propia, mira qué
    /// es y la borra. Solo se borra esa carpeta, que este método crea.
    /// </summary>
    public static async Task<(RecipeTest? Result, ApiError? Error)> Test(Database db, string id, string? token, CancellationToken ct = default)
    {
        if (Get(db, id) is not { } json) return (null, ApiError.Of("recipes.notFound", ("id", id)));
        var r = JsonNode.Parse(json)!;
        var source = (string?)r["updates"]?["source"];
        var repo = (string?)r["updates"]?["repo"];
        var host = (string?)r["updates"]?["host"];
        var pattern = (string?)r["asset"];
        if (repo is null || pattern is null || !HasRepo(source, host)) return (null, ApiError.Of("recipes.noRepo"));
        var dir = Path.Combine(Path.GetTempPath(), "otteretro-catalogo-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var releases = await Releases(source, host, repo, token, ct);
            if (releases.Count == 0) return (null, ApiError.Of("recipes.noRelease", ("repo", repo)));
            var latest = releases[0];
            var name = latest.Assets.FirstOrDefault(a => Regex.IsMatch(a, pattern));
            if (name is null || latest.Urls?.GetValueOrDefault(name) is not { } url) return (null, ApiError.Of("recipes.noAsset", ("repo", repo), ("pattern", pattern)));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Path.GetFileName(name));
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)response.StatusCode}");
                await using var file = File.Create(path);
                await response.Content.CopyToAsync(file, ct);
            }
            var detect = (string?)r["detect"] ?? "";
            var wanted = detect.Replace('\\', '/').TrimEnd('/');
            var leaf = wanted[(wanted.LastIndexOf('/') + 1)..];
            bool? found = null;
            if (string.Equals(Path.GetFileName(path), leaf, StringComparison.OrdinalIgnoreCase)) found = true;
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                found = zip.Entries.Any(e => e.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase) || e.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase)
                    || e.FullName.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase));
            }
            return (new RecipeTest(latest.Tag, Path.GetFileName(path), new FileInfo(path).Length, detect, found), null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, ApiError.Of("recipes.sourceFailed", ("repo", repo), ("detail", e.Message)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Una fuente con releases que se leen por API: GitHub, o Forgejo, Gitea y GitLab con su servidor.</summary>
    internal static bool HasRepo([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? source, string? host) => source == "github" || source is "forgejo" or "gitlab" && host is not null;

    /// <summary>Las releases publicadas (sin borradores) de un repo de GitHub, o de Forgejo o Gitea (la misma API, en <paramref name="host"/>).</summary>
    internal static async Task<List<Release>> Releases(string source, string? host, string repo, string? token, CancellationToken ct)
    {
        if (source == "gitlab") return await GitLabReleases(host!, repo, ct);
        var url = source == "forgejo" ? $"https://{host}/api/v1/repos/{repo}/releases?limit=30" : $"https://api.github.com/repos/{repo}/releases?per_page=30";
        if (await Api(url, token, ct) is not JsonArray list) return [];
        return list.Where(g => g?["draft"]?.GetValue<bool>() != true)
            .Select(g => new Release((string?)g!["tag_name"] ?? "", (string?)g["published_at"], g["assets"]?.AsArray().Select(a => (string)a!["name"]!).ToList() ?? [],
                g["assets"]?.AsArray().Where(a => (string?)a!["browser_download_url"] is not null).ToDictionary(a => (string)a!["name"]!, a => (string)a!["browser_download_url"]!, StringComparer.Ordinal),
                g["prerelease"]?.GetValue<bool>() == true))
            .OrderByDescending(g => g.PublishedAt ?? "").ToList();
    }

    /// <summary>
    /// Las releases de GitLab (#155): sus archivos son los enlaces de cada release; upcoming_release (una release futura) cuenta como
    /// prerelease. Los paquetes del código fuente que GitLab agrega solo no son archivos.
    /// </summary>
    static async Task<List<Release>> GitLabReleases(string host, string repo, CancellationToken ct)
    {
        if (await Api($"https://{host}/api/v4/projects/{Uri.EscapeDataString(repo)}/releases?per_page=30", null, ct) is not JsonArray list) return [];
        return list.OfType<JsonObject>().Select(g =>
            {
                var links = g["assets"]?["links"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
                return new Release((string?)g["tag_name"] ?? "", (string?)(g["released_at"] ?? g["created_at"]), links.Select(a => (string)a["name"]!).ToList(),
                    links.DistinctBy(a => (string)a["name"]!).ToDictionary(a => (string)a["name"]!, a => (string?)a["direct_asset_url"] ?? (string)a["url"]!, StringComparer.Ordinal),
                    g["upcoming_release"]?.GetValue<bool>() == true);
            })
            .OrderByDescending(g => g.PublishedAt ?? "").ToList();
    }

    /// <summary>
    /// Un GET a la API de GitHub (con la clave, si hay) o de Forgejo o Gitea. null si es un 404; otro error lanza con su código, para que
    /// quien llama distinga un límite de consultas (403 o 429).
    /// </summary>
    internal static async Task<JsonNode?> Api(string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("OtteretroCatalogo (+https://github.com/" + GitHubPublisher.Repo + ")");
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal) && token is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)response.StatusCode}", null, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// La fuente de un repo pegado (como en Otteretro, #350): `github.com/dueño/repo` es GitHub; otro servidor, Forgejo o Gitea. Acepta con
    /// o sin https, `.git` al final y rutas de más (`/releases`). null si no es un repo.
    /// </summary>
    public static (string Source, string Repo, string? Host)? Source(string text)
    {
        var s = text.Trim();
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Contains('.')) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var repo = $"{parts[0]}/{(parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1])}";
        if (!RepoName().IsMatch(repo)) return null;
        if (uri.Host is "github.com" or "www.github.com") return ("github", repo, null);
        // shortcut: GitLab se reconoce por el nombre del servidor (gitlab.com, gitlab.algo, framagit); uno propio con otro nombre pasa por Forgejo.
        return (uri.Host == "framagit.org" || uri.Host == "gitlab.com" || uri.Host.StartsWith("gitlab.", StringComparison.Ordinal) ? "gitlab" : "forgejo", repo, uri.Authority);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoName();

    /// <summary>Archivos que no son el de Windows: otros sistemas, símbolos de depuración, código fuente, firmas (los mismos que en Otteretro).</summary>
    [GeneratedRegex(@"linux|macos|darwin|\bmac\b|osx|android|ios|appimage|flatpak|aarch64|arm64|symbol|debug|pdb|source|\.(deb|rpm|dmg|apk|sig|sha\d*|txt|json)$", RegexOptions.IgnoreCase)]
    internal static partial Regex NotWindows();

    /// <summary>
    /// El patrón de un archivo: su nombre con cada versión (1.2.3) cambiada por [0-9.]+. A diferencia de Otteretro (#350), solo los números
    /// con punto que no van pegados a letras (salvo la v de v1.2): «win64.7z» queda como está.
    /// </summary>
    public static string AssetPattern(string file) =>
        "^" + Regex.Replace(Regex.Escape(file), @"(?<![A-Za-uw-z0-9])\d+(\\\.\d+)+(?![A-Za-z0-9])", "[0-9.]+") + "$";

    /// <summary>«Agregar una receta» (#110): las releases del repo pegado con los archivos de cada una, para elegir el de Windows.</summary>
    public static async Task<(List<RepoRelease>? Releases, ApiError? Error)> RepoReleases(string url, string? token, CancellationToken ct = default)
    {
        if (Source(url) is not { } parsed) return (null, ApiError.Of("recipes.badRepo"));
        var (source, repo, host) = parsed;
        try
        {
            var list = (await Releases(source, host, repo, token, ct)).Select(r => new RepoRelease(r.Tag, r.PublishedAt,
                r.Assets.Select(a => new RepoFile(a, !NotWindows().IsMatch(a), AssetPattern(a))).ToList())).ToList();
            return list.Count == 0 ? (null, ApiError.Of("recipes.noRelease", ("repo", repo))) : (list, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, ApiError.Of("recipes.sourceFailed", ("repo", repo), ("detail", e.Message)));
        }
    }

    /// <summary>
    /// «Crear desde su repo»: una receta para completar a partir de la release más nueva con un archivo de Windows. Lleva su fuente, el
    /// patrón del archivo y un ejecutable supuesto; la consola, la carpeta y lo demás se completan en el formulario. No se guarda.
    /// </summary>
    public static async Task<(string? Json, ApiError? Error)> Draft(string url, string? token, CancellationToken ct = default, string? chosen = null)
    {
        if (Source(url) is not { } parsed) return (null, ApiError.Of("recipes.badRepo"));
        var (source, repo, host) = parsed;
        List<Release> releases;
        try { releases = await Releases(source, host, repo, token, ct); }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, ApiError.Of("recipes.sourceFailed", ("repo", repo), ("detail", e.Message)));
        }
        var file = chosen ?? releases.Select(r => r.Assets.Where(a => !NotWindows().IsMatch(a)).OrderBy(a => a.Contains("win", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault())
            .FirstOrDefault(a => a is not null);
        if (file is null) return (null, ApiError.Of("recipes.noRelease", ("repo", repo)));
        var name = repo.Split('/')[1];
        var updates = new JsonObject { ["source"] = source, ["repo"] = repo };
        if (host is not null) updates["host"] = host;
        var recipe = new JsonObject
        {
            ["id"] = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-'),
            ["name"] = name,
            ["kind"] = "emulator",
            ["folder"] = "",
            ["detect"] = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : name + ".exe",
            ["consoles"] = new JsonArray(),
            ["updates"] = updates,
            ["asset"] = AssetPattern(file),
        };
        return (recipe.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n", null);
    }
}

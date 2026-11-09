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
public record RecipeInfo(string Id, string Name, string Kind, string Folder, List<string> Consoles, bool Local = false);
/// <summary>Una receta entera, como JSON.</summary>
public record RecipeText(string Json);
/// <summary>La URL del repo de un emulador (GitHub, Forgejo o Gitea).</summary>
public record RepoRequest(string Url);

/// <summary>
/// La sección «Emuladores» (#95, decisiones 352 y 353 de Otteretro). Las recetas se guardan en la base local (`catalog.recipe`), como las
/// sagas: `Published.Write` las escribe en `recetas/&lt;id&gt;.json` y Publicar lleva al PR las que cambiaron. Se guarda solo una receta que
/// cumple el esquema y cuya fuente tiene una release con un archivo que calza con su patrón (`asset`).
/// </summary>
public static partial class Recipes
{
    /// <summary>Para pedir las releases; las pruebas lo cambian por uno falso.</summary>
    public static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static RecipeInfo Info(string json, bool local)
    {
        var r = JsonNode.Parse(json)!;
        return new RecipeInfo((string)r["id"]!, (string?)r["name"] ?? "", (string?)r["kind"] ?? "", (string?)r["folder"] ?? "",
            r["consoles"]?.AsArray().Select(x => (string)x!).ToList() ?? [], local);
    }

    public static List<RecipeInfo> List(Database db, string? local = null)
    {
        using var c = db.Open();
        return c.Query<string>("SELECT json FROM recipe ORDER BY id").Select(json => Info(json, false))
            .Concat(Local(db, local).Values.Select(json => Info(json, true))).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
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

    static void Store(System.Data.IDbConnection c, JsonElement r, string text)
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
    public static async Task<ApiError?> Save(Database db, string id, string json, bool create, string? token, CancellationToken ct = default)
    {
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
            if (await CheckRelease(r, token, ct) is { } error) return error;
            // Sin cambios, conserva sus bytes (los de main): así no aparece como cambiada solo por el formato.
            bool same;
            using (var old = before is null ? null : JsonDocument.Parse(before)) same = old is not null && JsonElement.DeepEquals(old.RootElement, r);
            using var c = db.Open();
            Store(c, r, same ? before! : JsonSerializer.Serialize(r, Indented).ReplaceLineEndings("\n") + "\n");
            return null;
        }
    }

    /// <summary>
    /// Que la fuente de la receta tenga una release con un archivo que calce con <c>asset</c>. Sin <c>asset</c> o sin <c>updates</c> no hay
    /// qué revisar; <c>dolphin</c> publica en su propio servidor, sin releases.
    /// </summary>
    static async Task<ApiError?> CheckRelease(JsonElement r, string? token, CancellationToken ct)
    {
        if (!r.TryGetProperty("asset", out var asset) || !r.TryGetProperty("updates", out var u)) return null;
        var source = u.GetProperty("source").GetString()!;
        if (source is not ("github" or "forgejo")) return null;
        var repo = u.TryGetProperty("repo", out var x) ? x.GetString() : null;
        var host = u.TryGetProperty("host", out var h) ? h.GetString() : null;
        if (repo is null || source == "forgejo" && host is null) return ApiError.Of("recipes.noRepo");
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

    sealed record Release(string Tag, string? PublishedAt, List<string> Assets);

    /// <summary>Las releases publicadas (sin borradores) de un repo de GitHub, o de Forgejo o Gitea (la misma API, en <paramref name="host"/>).</summary>
    static async Task<List<Release>> Releases(string source, string? host, string repo, string? token, CancellationToken ct)
    {
        var url = source == "forgejo" ? $"https://{host}/api/v1/repos/{repo}/releases?limit=30" : $"https://api.github.com/repos/{repo}/releases?per_page=30";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("OtteretroCatalogo (+https://github.com/" + GitHubPublisher.Repo + ")");
        if (source == "github" && token is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)response.StatusCode}");
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsArray();
        return list.Where(g => g?["draft"]?.GetValue<bool>() != true)
            .Select(g => new Release((string?)g!["tag_name"] ?? "", (string?)g["published_at"], g["assets"]?.AsArray().Select(a => (string)a!["name"]!).ToList() ?? []))
            .OrderByDescending(g => g.PublishedAt ?? "").ToList();
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
        return uri.Host is "github.com" or "www.github.com" ? ("github", repo, null) : ("forgejo", repo, uri.Authority);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoName();

    /// <summary>Archivos que no son el de Windows: otros sistemas, símbolos de depuración, código fuente, firmas (los mismos que en Otteretro).</summary>
    [GeneratedRegex(@"linux|macos|darwin|\bmac\b|osx|android|ios|appimage|flatpak|aarch64|arm64|symbol|debug|pdb|source|\.(deb|rpm|dmg|apk|sig|sha\d*|txt|json)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotWindows();

    /// <summary>
    /// El patrón de un archivo: su nombre con cada versión (1.2.3) cambiada por [0-9.]+. A diferencia de Otteretro (#350), solo los números
    /// con punto que no van pegados a letras (salvo la v de v1.2): «win64.7z» queda como está.
    /// </summary>
    public static string AssetPattern(string file) =>
        "^" + Regex.Replace(Regex.Escape(file), @"(?<![A-Za-uw-z0-9])\d+(\\\.\d+)+(?![A-Za-z0-9])", "[0-9.]+") + "$";

    /// <summary>
    /// «Crear desde su repo»: una receta para completar a partir de la release más nueva con un archivo de Windows. Lleva su fuente, el
    /// patrón del archivo y un ejecutable supuesto; la consola, la carpeta y lo demás se completan en el formulario. No se guarda.
    /// </summary>
    public static async Task<(string? Json, ApiError? Error)> Draft(string url, string? token, CancellationToken ct = default)
    {
        if (Source(url) is not { } parsed) return (null, ApiError.Of("recipes.badRepo"));
        var (source, repo, host) = parsed;
        List<Release> releases;
        try { releases = await Releases(source, host, repo, token, ct); }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, ApiError.Of("recipes.sourceFailed", ("repo", repo), ("detail", e.Message)));
        }
        var file = releases.Select(r => r.Assets.Where(a => !NotWindows().IsMatch(a)).OrderBy(a => a.Contains("win", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault())
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

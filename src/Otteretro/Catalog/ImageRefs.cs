using System.Collections.Concurrent;
using Dapper;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <summary>
/// Las imágenes van por referencia (#6, #215): el catálogo guarda la dirección de cada una en SteamGridDB o IGDB, y la primera vez que se
/// muestra se baja, sin clave, a la carpeta de imágenes. Desde ahí se sirve siempre, también sin conexión.
/// </summary>
public static class ImageRefs
{
    /// <summary>Solo de donde salen las imágenes del catálogo: una referencia no puede apuntar a cualquier sitio.</summary>
    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host == "images.igdb.com" || u.Host.EndsWith(".steamgriddb.com", StringComparison.Ordinal));

    /// <summary>Para las pruebas: responde en lugar de SteamGridDB e IGDB.</summary>
    public static HttpMessageHandler? Handler { get; set; }
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    /// <summary>Una saga abre decenas a la vez: de a pocas, para no saturar la conexión ni a quien las sirve.</summary>
    static readonly SemaphoreSlim Slots = new(4);
    /// <summary>La misma imagen pedida dos veces a la vez se baja una sola.</summary>
    static readonly ConcurrentDictionary<string, Task<string?>> Running = new();

    public static string? Url(Database db, long gameId, string kind)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT url FROM catalog.image WHERE game_id = @gameId AND kind = @kind", new { gameId, kind });
    }

    /// <summary>El archivo de la imagen, bajándolo si hace falta; null si no hay referencia o no se pudo bajar (se ve el color de la consola).</summary>
    public static async Task<string?> File(Database db, long gameId, string kind, CancellationToken ct = default)
    {
        if (SteamGridDb.Find(db, gameId, kind) is { } file) return file;
        if (Url(db, gameId, kind) is not { } url || !IsAllowed(url)) return null;
        var key = $"{SteamGridDb.Folder(db)}|{gameId}|{kind}";
        // Task.Run: la descarga empieza después de quedar registrada, así su `finally` siempre la saca.
        return await Running.GetOrAdd(key, _ => Task.Run(() => Download(db, gameId, kind, url, key))).WaitAsync(ct);
    }

    static async Task<string?> Download(Database db, long gameId, string kind, string url, string key)
    {
        await Slots.WaitAsync();
        try
        {
            var http = Handler is null ? Shared : new HttpClient(Handler);
            var bytes = await http.GetByteArrayAsync(url);
            var webp = SteamGridDb.ToWebp(bytes, kind);
            var folder = SteamGridDb.Folder(db);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{gameId}-{kind}{(webp is null ? Path.GetExtension(new Uri(url).AbsolutePath) : ".webp")}");
            SteamGridDb.Write(path, webp ?? bytes);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return null; }
        finally
        {
            Slots.Release();
            Running.TryRemove(key, out _);
        }
    }

    /// <summary>Borra lo bajado cuya referencia cambió o desapareció: la próxima vez se baja la nueva.</summary>
    public static void Forget(Database db, IEnumerable<(long GameId, string Kind)> changed)
    {
        foreach (var (gameId, kind) in changed)
            while (SteamGridDb.Find(db, gameId, kind) is { } file) System.IO.File.Delete(file);
    }
}

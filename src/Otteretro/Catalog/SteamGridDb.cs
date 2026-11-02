using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;
using SkiaSharp;

namespace Otteretro.Catalog;

/// <summary>
/// Portadas 2:3, héroes y logos de SteamGridDB (F1.7) en `catalogo/imagenes/{juego}-{tipo}.{ext}` (van con el catálogo, #167).
/// La tarea `sgdb.images` recorre los juegos sin portada, con punto de control; la encola cada carga de IGDB.
/// Si SteamGridDB no tiene una portada segura del juego, se usa la carátula oficial de IGDB (#61).
/// Se guardan en WebP y al tamaño que muestra la app (#64); `sgdb.webp` convierte las que se bajaron antes.
/// </summary>
public static class SteamGridDb
{
    public const string ApiUrl = "https://www.steamgriddb.com/api/v2/";
    public static readonly string[] Kinds = ["cover", "hero", "logo"];

    /// <summary>Qué se pide de cada tipo: portada vertical 2:3 (como la de Steam), héroe y logo.</summary>
    static readonly Dictionary<string, string> Queries = new()
    {
        ["cover"] = "grids/game/{0}?dimensions=600x900,342x482,660x930",
        ["hero"] = "heroes/game/{0}",
        ["logo"] = "logos/game/{0}",
    };

    /// <summary>Tope de cada tipo (#64): lo que muestra la app. Se reduce sin agrandar y sin cambiar la proporción.</summary>
    static readonly Dictionary<string, (int Width, int Height)> MaxSize = new()
    {
        ["cover"] = (600, 900),
        ["hero"] = (1920, 620),
        ["logo"] = (800, int.MaxValue),
    };

    static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Para las pruebas: el manejador HTTP falso de SteamGridDB y su CDN.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    public record Job(long After = 0);
    record Found(long Id, string? Name = null, [property: JsonPropertyName("release_date")] long? ReleaseDate = null);
    record IgdbCover(string? ImageId);

    /// <summary>Carátula de IGDB (528×748): https://api-docs.igdb.com/#images.</summary>
    public const string IgdbImageUrl = "https://images.igdb.com/igdb/image/upload/t_cover_big_2x/";
    record Image(string Url);
    record Response<T>(bool Success, T? Data);

    /// <summary>Ajustes › Consolas › Portadas (#132): cuántos juegos tienen portada, si hay una descarga en cola o corriendo y si hay clave.</summary>
    public record CoversInfo(long Have, long Total, bool Running, bool HasKey);

    public static CoversInfo Status(Database db, bool hasKey)
    {
        using var c = db.Open();
        var games = c.Query<long>("SELECT id FROM game").ToHashSet();
        var running = c.ExecuteScalar<long>("SELECT count(*) FROM task WHERE kind = 'sgdb.images' AND status IN ('queued', 'running')") > 0;
        return new(CatalogQueries.Covers(db).Count(games.Contains), games.Count, running, hasKey);
    }

    /// <summary>«Bajar portadas»: encola la descarga desde el principio (salta los que ya tienen), salvo que ya haya una.</summary>
    public static CoversInfo Request(Database db, bool hasKey)
    {
        if (hasKey && !Status(db, hasKey).Running) Enqueue(db);
        return Status(db, hasKey);
    }

    public static long Enqueue(Database db, long after = 0) =>
        TaskQueue.Enqueue(db, "sgdb.images", JsonSerializer.Serialize(new Job(after), JsonSerializerOptions.Web));

    /// <summary>Encola la conversión a WebP si quedan imágenes en otro formato y no hay una esperando.</summary>
    public static void EnqueueConvert(Database db)
    {
        if (Unconverted(db).Length == 0) return;
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT count(*) FROM task WHERE kind = 'sgdb.webp' AND status IN ('queued', 'running')") == 0)
            TaskQueue.Enqueue(db, "sgdb.webp");
    }

    /// <summary>Lo que falta convertir a WebP. Con el ★ de Otteretro no se convierte aquí: es Otteretro quien lo hace (#9).</summary>
    static string[] Unconverted(Database db) => ImageHome.Info(db) is { ManagedByOtteretro: false, Available: true, Folder: { } folder } && Directory.Exists(folder)
        ? Directory.GetFiles(folder).Where(f => Path.GetExtension(f) != ".webp" && !Path.GetFileName(f).StartsWith('.')).Order().ToArray()
        : [];

    /// <summary>La carpeta de imágenes: `catalogo\imagenes` del disco ★ (#9), o null si todavía no hay ★.</summary>
    public static string? Folder(Database db) => ImageHome.Folder(db);

    /// <summary>
    /// Las imágenes de un juego que deja de existir se quedan (#9): la carpeta es la de Otteretro, que todavía puede tener ese juego
    /// hasta que recibe el catálogo nuevo, y los ids publicados no se reutilizan.
    /// </summary>
    public static void Forget(Database db, long gameId) { }

    /// <summary>La imagen guardada de un juego, o null si todavía no hay.</summary>
    public static string? Find(Database db, long gameId, string kind) =>
        Folder(db) is { } folder && Directory.Exists(folder) ? Directory.EnumerateFiles(folder, $"{gameId}-{kind}.*").FirstOrDefault() : null;

    public static async Task Images(TaskRun run, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<Job>(run.Data!, JsonSerializerOptions.Web)!;
        var key = run.Credentials.Read("steamgriddb.apiKey");
        if (string.IsNullOrEmpty(key)) throw new TaskException(ApiError.Of("tasks.sgdb.noKey"));

        var api = new ApiClient(run.Db, Handler);
        var http = Handler is null ? Shared : new HttpClient(Handler);
        var home = ImageHome.Info(run.Db);
        if (home.Folder is not { } folder) throw new TaskException(ApiError.Of("images.noStar"));
        // Un disco desconectado no se reemplaza por una carpeta nueva vacía (#9).
        if (!home.Available) throw new TaskException(ApiError.Of("images.folderMissing", ("path", home.Star!)));
        Directory.CreateDirectory(folder);
        EnqueueConvert(run.Db);

        var igdb = !string.IsNullOrEmpty(run.Credentials.Read("igdb.clientId")) && !string.IsNullOrEmpty(run.Credentials.Read("igdb.clientSecret"))
            ? new Igdb(api, run.Credentials, Handler) : null;

        List<(long Id, string Name, long? Steam, long? Year, long? IgdbId)> games;
        using (var c = run.Db.Open())
            games = c.Query<(long, string, long?, long?, long?)>("""
                SELECT g.id, COALESCE(g.original_name, g.name_es), (SELECT max(steam_app_id) FROM version v WHERE v.game_id = g.id), g.year, g.igdb_id
                FROM game g WHERE g.id > @After ORDER BY g.id
                """, job).ToList();
        var last = games.Count > 0 ? games[^1].Id : job.After;
        games = games.Where(g => Find(run.Db, g.Id, "cover") is null).ToList();

        for (var i = 0; i < games.Count; i++)
        {
            var (id, name, steam, year, igdbId) = games[i];
            var got = new List<(string Kind, int Bytes)>();
            var sgdbId = await GameId(api, key, name, year, steam, ct);
            if (sgdbId is not null)
                foreach (var kind in Kinds)
                    if (Find(run.Db, id, kind) is null && await First(api, key, string.Format(Queries[kind], sgdbId), ct) is { } url)
                        got.Add((kind, await Download(http, url, folder, id, kind, ct)));
            // ponytail: una consulta a IGDB por juego sin portada; de a 500 si la carga completa se hace lenta.
            if (Find(run.Db, id, "cover") is null && igdb is not null && igdbId is not null
                && (await igdb.Query<IgdbCover>("covers", $"fields image_id; where game = {igdbId}; limit 1;", ct)).FirstOrDefault()?.ImageId is { } image)
                got.Add(("cover", await Download(http, IgdbImageUrl + image + ".jpg", folder, id, "cover", ct)));
            run.Log(got.Count == 0 ? ApiError.Of("tasks.log.noImages", ("game", name))
                : ApiError.Of("tasks.log.images", ("game", name), ("kinds", string.Join(", ", got.Select(g => g.Kind))), ("kb", (got.Sum(g => g.Bytes) / 1024).ToString())));
            // La carga corre a la par: si el juego se fusionó y se borró mientras bajaba, su imagen no queda para el próximo id (#59).
            using (var c = run.Db.Open())
                if (c.ExecuteScalar<long?>("SELECT id FROM game WHERE id = @id", new { id }) is null) Forget(run.Db, id);
            run.Report((i + 1) * 100 / games.Count, ApiError.Of("tasks.sgdb.images", ("done", (i + 1).ToString()), ("total", games.Count.ToString())),
                JsonSerializer.Serialize(new Job(id), JsonSerializerOptions.Web));
        }

        // Lo que cargó IGDB mientras esta corría (#78): otra pasada solo desde el último juego de la lista.
        using (var c = run.Db.Open())
            if (c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE id > @last", new { last }) > 0
                && c.ExecuteScalar<long>("SELECT count(*) FROM task WHERE kind = 'sgdb.images' AND status = 'queued'") == 0)
                Enqueue(run.Db, last);
    }

    /// <summary>
    /// El juego en SteamGridDB: por el id de Steam si lo hay; si no, el resultado de la búsqueda con el mismo nombre y,
    /// si los dos tienen año, el mismo o uno de diferencia. Si ninguno coincide, null: una portada de otro juego es peor que ninguna (#61).
    /// </summary>
    static async Task<long?> GameId(ApiClient api, string key, string name, long? year, long? steam, CancellationToken ct)
    {
        if (steam is not null && await Get<Found>(api, key, $"games/steam/{steam}", ct) is { } bySteam) return bySteam.Id;
        var list = await Get<Found[]>(api, key, $"search/autocomplete/{Uri.EscapeDataString(name)}", ct) ?? [];
        var wanted = IgdbImport.NameKey(name);
        return list.FirstOrDefault(f => f.Name is not null && IgdbImport.NameKey(f.Name) == wanted
            && (year is null || f.ReleaseDate is not { } date || Math.Abs(DateTimeOffset.FromUnixTimeSeconds(date).Year - year.Value) <= 1))?.Id;
    }

    static async Task<string?> First(ApiClient api, string key, string path, CancellationToken ct) =>
        (await Get<Image[]>(api, key, path, ct))?.FirstOrDefault()?.Url;

    /// <summary>Un 404 (juego sin ficha o sin imágenes) es «no hay», no un error.</summary>
    static async Task<T?> Get<T>(ApiClient api, string key, string path, CancellationToken ct)
    {
        try
        {
            var text = await api.Send("steamgriddb", HttpMethod.Get, ApiUrl + path, null, Ttl,
                r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key), ct);
            return JsonSerializer.Deserialize<Response<T>>(text, JsonSerializerOptions.Web) is { Success: true } r ? r.Data : default;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new TaskException(ApiError.Of("tasks.sgdb.badKey"));
        }
    }

    /// <summary>Baja la imagen y la guarda en WebP reducida; si no se puede leer como imagen, tal como vino (#64).</summary>
    /// <returns>Lo que pesa en disco.</returns>
    static async Task<int> Download(HttpClient http, string url, string folder, long id, string kind, CancellationToken ct)
    {
        var bytes = await http.GetByteArrayAsync(url, ct);
        var webp = ToWebp(bytes, kind);
        Write(Path.Combine(folder, $"{id}-{kind}{(webp is null ? Path.GetExtension(new Uri(url).AbsolutePath) : ".webp")}"), webp ?? bytes);
        return (webp ?? bytes).Length;
    }

    /// <summary>Escribe a un temporal y lo renombra al final: un corte no deja una imagen rota.</summary>
    static void Write(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".part");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>La imagen leída, o null si no es una imagen (SKBitmap.Decode lanza en vez de devolver null).</summary>
    static SKBitmap? Read(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec is null ? null : SKBitmap.Decode(codec);
    }

    /// <summary>La imagen en WebP (calidad 80) y dentro del tope de su tipo, o null si no es una imagen que se pueda leer.</summary>
    public static byte[]? ToWebp(byte[] bytes, string kind)
    {
        using var source = Read(bytes);
        if (source is null) return null;
        var (width, height) = MaxSize[kind];
        var scale = Math.Min(1, Math.Min((double)width / source.Width, (double)height / source.Height));
        using var resized = scale < 1
            ? source.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
            : null;
        using var image = SKImage.FromBitmap(resized ?? source);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 80);
        return data?.ToArray();
    }

    /// <summary>
    /// `sgdb.webp` (#64): convierte lo ya bajado sin volver a descargarlo. El original se borra solo cuando el WebP quedó escrito
    /// y se abre bien; lo que no se puede leer se deja como está. Si se corta, al volver sigue con lo que falta.
    /// </summary>
    public static Task Convert(TaskRun run, CancellationToken ct)
    {
        var files = Unconverted(run.Db);
        using var c = run.Db.Open();
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.IndexOf('-');
            if (dash > 0 && long.TryParse(name[..dash], out var id) && MaxSize.ContainsKey(name[(dash + 1)..]))
            {
                // Una imagen de un juego que ya no existe se la quedaría el próximo juego con ese id (#59).
                if (c.ExecuteScalar<long?>("SELECT id FROM game WHERE id = @id", new { id }) is null) File.Delete(file);
                else if (ToWebp(File.ReadAllBytes(file), name[(dash + 1)..]) is { } webp)
                {
                    var target = Path.ChangeExtension(file, ".webp");
                    Write(target, webp);
                    using var check = Read(File.ReadAllBytes(target));
                    if (check is null) File.Delete(target);
                    else File.Delete(file);
                }
            }
            if ((i + 1) % 50 == 0 || i == files.Length - 1)
                run.Report((i + 1) * 100 / files.Length, ApiError.Of("tasks.sgdb.webp", ("done", (i + 1).ToString()), ("total", files.Length.ToString())));
        }
        return Task.CompletedTask;
    }
}

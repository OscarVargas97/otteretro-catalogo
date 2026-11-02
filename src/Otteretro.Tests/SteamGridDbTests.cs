using System.Net;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Otteretro.App;
using Otteretro.Catalog;
using SkiaSharp;

namespace Otteretro.Tests;

/// <summary>F1.7 con un SteamGridDB falso.</summary>
[Collection("FakeHttp")]
public class SteamGridDbTests
{
    sealed class FakeSteamGridDb : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            // IGDB (respaldo de la portada, #61): el token y la carátula del juego 3.
            if (url == Igdb.TokenUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "access_token": "token", "expires_in": 3600 }""") });
            if (url == Igdb.ApiUrl + "covers")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[ { "id": 1, "image_id": "co3nada" } ]""") });
            if (url.StartsWith("https://cdn2.steamgriddb.com/") || url.StartsWith(SteamGridDb.IgdbImageUrl))
            {
                OnDownload?.Invoke(url);
                // a.png es una imagen de verdad y grande (#64); lo demás no se puede leer como imagen y queda tal cual.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(url.EndsWith("/a.png") ? Png(1200, 1800) : [1, 2, 3]) });
            }
            var path = url[SteamGridDb.ApiUrl.Length..];
            string? data = path switch
            {
                "games/steam/70" => """{ "id": 11 }""",
                "search/autocomplete/Zelda%20Classic" => """[ { "id": 22, "name": "Zelda: Classic", "release_date": 1200000000 } ]""",
                // El primer resultado es otro juego (#61): no se usa.
                "search/autocomplete/Nada" => """[ { "id": 33, "name": "Nada Mas", "release_date": 1200000000 } ]""",
                _ when path.StartsWith("grids/game/11") => """[ { "url": "https://cdn2.steamgriddb.com/grid/a.png" } ]""",
                _ when path.StartsWith("grids/game/22") => """[ { "url": "https://cdn2.steamgriddb.com/grid/b.jpg" } ]""",
                "logos/game/11" => """[ { "url": "https://cdn2.steamgriddb.com/logo/c.webp" } ]""",
                "heroes/game/22" or "logos/game/22" => "[]",
                _ => null, // heroes/game/11: SteamGridDB responde 404
            };
            return Task.FromResult(data is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{ "success": false }""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{ "success": true, "data": {{data}} }""") });
        }
    }

    /// <summary>Un PNG del tamaño pedido; con `transparent`, la mitad izquierda transparente.</summary>
    static byte[] Png(int width, int height, bool transparent = false)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            if (transparent) canvas.DrawRect(0, 0, width / 2, height, new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src });
        }
        return bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>#64: se reduce al tope de su tipo sin agrandar ni deformar, y el logo conserva la transparencia.</summary>
    [Fact]
    public void ImagesAreSavedAsSmallerWebp()
    {
        var big = Png(1200, 1800);
        var cover = SteamGridDb.ToWebp(big, "cover")!;
        using (var decoded = SKBitmap.Decode(cover))
            Assert.Equal((600, 900), (decoded.Width, decoded.Height));
        Assert.True(cover.Length < big.Length);

        using (var small = SKBitmap.Decode(SteamGridDb.ToWebp(Png(300, 450), "cover")))
            Assert.Equal((300, 450), (small.Width, small.Height));

        using var logo = SKBitmap.Decode(SteamGridDb.ToWebp(Png(1600, 400, transparent: true), "logo"));
        Assert.Equal((800, 200), (logo.Width, logo.Height));
        Assert.Equal(0, logo.GetPixel(10, 100).Alpha);
        Assert.Equal(255, logo.GetPixel(790, 100).Alpha);

        Assert.Null(SteamGridDb.ToWebp([1, 2, 3], "cover"));
    }

    /// <summary>
    /// #64: lo bajado antes pasa a WebP sin descargar nada; lo que no se puede leer queda como está y la imagen de un juego
    /// que ya no existe se borra (#59).
    /// </summary>
    [Fact]
    public async Task OldImagesAreConvertedWithoutDownloading()
    {
        await using var f = new Fixture();
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO game (id, name_es, original_name) VALUES (1, 'Zelda', 'Zelda')");
        var folder = SteamGridDb.Folder(f.Db);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "1-cover.png"), Png(1200, 1800));
        File.WriteAllBytes(Path.Combine(folder, "1-hero.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "9-cover.png"), Png(10, 10));
        OnDownload = url => throw new InvalidOperationException("no se descarga nada");

        SteamGridDb.EnqueueConvert(f.Db);
        SteamGridDb.EnqueueConvert(f.Db); // ya hay una esperando: no se duplica
        using (var c = f.Db.Open())
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT count(*) FROM task WHERE kind = 'sgdb.webp'"));
        long id;
        using (var c = f.Db.Open())
            id = c.ExecuteScalar<long>("SELECT id FROM task WHERE kind = 'sgdb.webp'");

        var task = await f.Run(id);
        Assert.Equal(("done", 100), (task.Status, task.Progress));
        Assert.Equal(["1-cover.webp", "1-hero.png"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        using var cover = SKBitmap.Decode(Path.Combine(folder, "1-cover.webp"));
        Assert.Equal((600, 900), (cover.Width, cover.Height));
    }

    /// <summary>Para simular algo que pasa mientras se baja una imagen.</summary>
    static Action<string>? OnDownload;

    public SteamGridDbTests()
    {
        SteamGridDb.Handler = new FakeSteamGridDb();
        OnDownload = null;
    }

    /// <summary>#59: si el juego se borra (fusión) mientras se baja su imagen, la imagen no queda para el próximo juego con ese id.</summary>
    [Fact]
    public async Task ImageOfAGameDeletedMeanwhileIsNotKept()
    {
        await using var f = new Fixture(("steamgriddb.apiKey", "clave"));
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO game (id, name_es, original_name) VALUES (2, 'Zelda Classic', 'Zelda Classic')");
        OnDownload = url =>
        {
            using var c = f.Db.Open();
            c.Execute("DELETE FROM game WHERE id = 2");
        };
        Assert.Equal("done", (await f.Run(SteamGridDb.Enqueue(f.Db))).Status);
        Assert.Null(SteamGridDb.Find(f.Db, 2, "cover"));
    }

    /// <summary>#78: un juego que entra mientras se bajan portadas queda para otra pasada que empieza después del último de la lista.</summary>
    [Fact]
    public async Task GamesLoadedMeanwhileGetAnotherPassFromWhereItEnded()
    {
        await using var f = new Fixture(("steamgriddb.apiKey", "clave"));
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO game (id, name_es, original_name) VALUES (2, 'Zelda Classic', 'Zelda Classic')");
        OnDownload = url =>
        {
            using var c = f.Db.Open();
            c.Execute("INSERT OR IGNORE INTO game (id, name_es, original_name) VALUES (5, 'Nada', 'Nada')");
        };
        var first = SteamGridDb.Enqueue(f.Db);
        Assert.Equal("done", (await f.Run(first)).Status);
        using var after = f.Db.Open(); // la cola puede haberla empezado ya: se mira cualquiera que no sea la primera
        var next = after.QuerySingle<string>("SELECT data FROM task WHERE kind = 'sgdb.images' AND id <> @first", new { first });
        // Empieza en 2 (o ya avanzó si la cola la corrió): nunca desde 0, que volvería a consultar todo.
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<SteamGridDb.Job>(next, System.Text.Json.JsonSerializerOptions.Web)!.After >= 2);
    }

    /// <summary>Listo cuando de F1.7: las portadas 2:3 quedan en la caché y la API las sirve.</summary>
    [Fact]
    public async Task CoversGoToTheCacheAndTheApiServesThem()
    {
        await using var f = new Fixture(("steamgriddb.apiKey", "clave"));
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO game (id, name_es, original_name) VALUES (1, 'Zelda PC', 'Zelda PC'), (2, 'Zelda Classic', 'Zelda Classic'), (3, 'Nada', 'Nada');
                INSERT INTO version (game_id, platform, steam_app_id) VALUES (1, 'pc', 70);
                """);

        var task = await f.Run(SteamGridDb.Enqueue(f.Db));
        Assert.Equal(("done", 100), (task.Status, task.Progress));
        var files = Directory.GetFiles(SteamGridDb.Folder(f.Db)).Select(Path.GetFileName).Order();
        Assert.Equal(["1-cover.webp", "1-logo.webp", "2-cover.jpg"], files);

        var app = LocalServer.Create(f.Session, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var http = app.GetTestClient();
        http.BaseAddress = new Uri("http://127.0.0.1/");
        var cover = await http.GetAsync($"/api/images/1/cover?key={f.Session.Key}");
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/webp", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/images/3/cover?key={f.Session.Key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/images/1/..%2F..%2Fdata?key={f.Session.Key}")).StatusCode);
        await app.StopAsync();
    }

    /// <summary>
    /// #61: sin id de Steam solo vale un resultado con el mismo nombre y año (±1). Si ninguno coincide, la portada sale de IGDB,
    /// que viene con el id del juego; sin cuenta de IGDB, el juego queda sin portada antes que con la de otro.
    /// </summary>
    [Fact]
    public async Task WithoutAnExactMatchTheCoverComesFromIgdb()
    {
        await using var f = new Fixture(("steamgriddb.apiKey", "clave"), ("igdb.clientId", "id"), ("igdb.clientSecret", "secreto"));
        using (var c = f.Db.Open())
            c.Execute("""
                INSERT INTO game (id, name_es, original_name, year, igdb_id) VALUES
                    (2, 'Zelda Classic', 'Zelda Classic', 1990, NULL), -- mismo nombre, otro año (2008), sin IGDB
                    (3, 'Nada', 'Nada', 2008, 3000),                    -- la búsqueda solo trae «Nada Mas»
                    (4, 'Zelda Classic', 'Zelda Classic', 2007, NULL);  -- mismo nombre y un año de diferencia
                """);

        Assert.Equal("done", (await f.Run(SteamGridDb.Enqueue(f.Db))).Status);
        var files = Directory.GetFiles(SteamGridDb.Folder(f.Db)).Select(Path.GetFileName).Order();
        Assert.Equal(["3-cover.jpg", "4-cover.jpg"], files);
    }

    [Fact]
    public async Task WithoutKeyTheTaskSaysWhy()
    {
        await using var f = new Fixture();
        Assert.Equal("tasks.sgdb.noKey", (await f.Run(SteamGridDb.Enqueue(f.Db))).Detail!.Code);
    }

    /// <summary>#132: «Bajar portadas» cuenta las que hay, encola una sola descarga y sin clave no encola.</summary>
    [Fact]
    public async Task RequestingCoversQueuesOnlyOne()
    {
        await using var f = new Fixture();
        using (var c = f.Db.Open()) c.Execute("INSERT INTO game (id, name_es, original_name) VALUES (1, 'A', 'A'), (2, 'B', 'B'), (99, 'C', 'C')");
        Directory.CreateDirectory(SteamGridDb.Folder(f.Db));
        File.WriteAllBytes(Path.Combine(SteamGridDb.Folder(f.Db), "1-cover.webp"), [1]);
        File.WriteAllBytes(Path.Combine(SteamGridDb.Folder(f.Db), "500-cover.webp"), [1]); // de un juego que ya no está: no cuenta

        Assert.Equal(new SteamGridDb.CoversInfo(1, 3, false, false), SteamGridDb.Request(f.Db, hasKey: false));
        Assert.True(SteamGridDb.Request(f.Db, hasKey: true).Running);
        Assert.True(SteamGridDb.Request(f.Db, hasKey: true).Running);
        using var check = f.Db.Open();
        Assert.Equal(1, check.ExecuteScalar<long>("SELECT count(*) FROM task WHERE kind = 'sgdb.images'"));
    }
}

/// <summary>#9: la carpeta de imágenes común con Otteretro.</summary>
public class SharedImagesTests
{
    static Otteretro.Data.Database Db(TempDir tmp) => new(Path.Combine(tmp.Path, "data", "otteretro.db"), Path.Combine(tmp.Path, "catalogo", "catalogo.db"));

    [Fact]
    public void AnImageInTheSharedFolderIsFoundAndNeverDeleted()
    {
        using var tmp = new TempDir();
        var db = Db(tmp);
        var shared = Directory.CreateDirectory(Path.Combine(tmp.Path, "Otteretro", "catalogo", "imagenes")).FullName;
        var image = Path.Combine(shared, "7-cover.webp");
        File.WriteAllText(image, "portada");

        Assert.Null(SteamGridDb.Find(db, 7, "cover")); // la carpeta propia no la tiene
        SteamGridDb.SetSharedFolder(db, shared);
        Assert.Equal(image, SteamGridDb.Find(db, 7, "cover"));

        SteamGridDb.Forget(db, 7);
        Assert.True(File.Exists(image)); // fusionar o depurar no le quita imágenes a Otteretro

        SteamGridDb.SetSharedFolder(db, null);
        Assert.Null(SteamGridDb.Find(db, 7, "cover"));
        Assert.True(File.Exists(image));
    }

    [Fact]
    public void OwnFolderStillDeletesImagesOfAGameThatIsGone()
    {
        using var tmp = new TempDir();
        var db = Db(tmp);
        Directory.CreateDirectory(SteamGridDb.Folder(db));
        var image = Path.Combine(SteamGridDb.Folder(db), "7-cover.webp");
        File.WriteAllText(image, "portada");
        SteamGridDb.Forget(db, 7);
        Assert.False(File.Exists(image));
    }

    [Fact]
    public void TheChosenFolderSurvivesARestart()
    {
        using var tmp = new TempDir();
        SteamGridDb.SetSharedFolder(Db(tmp), tmp.Path);
        Assert.Equal(tmp.Path, SteamGridDb.SharedFolder(Db(tmp)));
    }

    [Fact]
    public void ListsOnlyDisksWithOtteretroAndSaysWhichHasImages()
    {
        using var tmp = new TempDir();
        var disk = (string name) => Directory.CreateDirectory(Path.Combine(tmp.Path, name)).FullName;
        var (withImages, onlyMark, empty) = (disk("d"), disk("e"), disk("f"));
        Directory.CreateDirectory(Path.Combine(withImages, "Otteretro", "catalogo", "imagenes"));
        File.WriteAllText(Path.Combine(withImages, "Otteretro", "catalogo", "imagenes", "1-cover.webp"), "x");
        Directory.CreateDirectory(Path.Combine(onlyMark, "Otteretro"));
        File.WriteAllText(Path.Combine(onlyMark, "Otteretro", "otteretro-ubicacion.json"), "{}");

        var disks = SteamGridDb.OtteretroDisks([withImages, onlyMark, empty]);

        Assert.Equal([withImages, onlyMark], disks.Select(d => d.Name));
        Assert.True(disks[0].HasImages);
        Assert.Equal(Path.Combine(withImages, "Otteretro", "catalogo", "imagenes"), disks[0].Folder);
        Assert.False(disks[1].HasImages);
    }
}

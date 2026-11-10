using System.Net;
using System.Text;
using Dapper;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#95: «Emuladores» guarda una receta solo si cumple el esquema y su fuente tiene una release con un archivo que calce.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparte Recipes.Http con EmulatorCheckTests
public class RecipesTests
{
    /// <summary>Releases falsas por URL pedida; lo que no está es un 404.</summary>
    sealed class FakeReleases(Dictionary<string, string> bodies, Dictionary<string, byte[]>? files = null) : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add(url);
            if (files?.TryGetValue(url, out var bytes) == true) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    const string Releases = "https://api.github.com/repos/mgba-emu/mgba/releases?per_page=30";
    const string MgbaReleases = """
        [
          { "tag_name": "0.11", "draft": true, "published_at": "2026-10-01", "assets": [{ "name": "mGBA-0.11-win64.7z" }] },
          { "tag_name": "0.10.5", "draft": false, "published_at": "2026-01-01", "assets": [{ "name": "mGBA-0.10.5-macos.dmg" }, { "name": "mGBA-0.10.5-win64.7z" }] }
        ]
        """;

    static string Recipe(string asset) => $$"""
        { "id": "mgba", "name": "mGBA", "kind": "emulator", "folder": "Game Boy Advance", "detect": "mGBA.exe", "consoles": ["gba", "gb"],
          "updates": { "source": "github", "repo": "mgba-emu/mgba" }, "asset": "{{asset}}" }
        """;

    [Fact]
    public async Task OnlyARecipeWithAMatchingReleaseIsSaved()
    {
        await using var f = new Fixture();
        Recipes.Http = new HttpClient(new FakeReleases(new() { [Releases] = MgbaReleases }));

        // No cumple el esquema: no se guarda y dice qué falla.
        var bad = await Recipes.Save(f.Db, "mgba", """{ "id": "mgba", "name": "mGBA", "kind": "emulador" }""", create: true, token: null);
        Assert.Equal("recipes.schema", bad!.Code);
        Assert.Contains("detect", bad.Params!["problems"]);
        // Ningún archivo de una release publicada calza (el del borrador no cuenta).
        var noAsset = await Recipes.Save(f.Db, "mgba", Recipe("^mGBA-0\\\\.11-win64\\\\.7z$"), create: true, token: null);
        Assert.Equal("recipes.noAsset", noAsset!.Code);
        Assert.Empty(Recipes.List(f.Db));

        Assert.Null(await Recipes.Save(f.Db, "mgba", Recipe("^mGBA-[0-9.]+-win64\\\\.7z$"), create: true, token: null));
        var saved = Assert.Single(Recipes.List(f.Db));
        Assert.Equal(["gba", "gb"], saved.Consoles);
        // Crear otra con el mismo id no la pisa.
        Assert.Equal("recipes.exists", (await Recipes.Save(f.Db, "mgba", Recipe("^mGBA-[0-9.]+-win64\\\\.7z$"), create: true, token: null))!.Code);

        // Publicar la lleva: Write la escribe en recetas/ y pasa la validación del PR.
        var folder = f.Tmp["publicado"];
        Published.Write(f.Db, folder);
        Assert.Contains("\"asset\": \"^mGBA-[0-9.]+-win64\\\\.7z$\"", File.ReadAllText(Path.Combine(folder, Published.RecipesFolder, "mgba.json")));
    }

    [Fact]
    public async Task ForGameListsTheRecipesThatRecommendIt()
    {
        await using var f = new Fixture();
        Recipes.Http = new HttpClient(new FakeReleases(new() { [Releases] = MgbaReleases }));
        var json = Recipe("^mGBA-[0-9.]+-win64\\\\.7z$").Replace("\"id\"", "\"recommendedFor\": [{ \"game\": \"X\", \"gameId\": 7, \"source\": \"wiki\" }, { \"game\": \"Y\" }], \"id\"");
        Assert.Null(await Recipes.Save(f.Db, "mgba", json, create: true, token: null));
        var r = Assert.Single(Recipes.ForGame(f.Db, 7));
        Assert.Equal(("mgba", "wiki"), (r.Id, r.Source));
        Assert.Empty(Recipes.ForGame(f.Db, 8));
    }

    /// <summary>#132: elegir a mano el recomendado de una consola lo saca de la receta que lo tenía; queda uno solo por juego y consola.</summary>
    [Fact]
    public async Task ChoosingARecommendedEmulatorReplacesThePreviousOne()
    {
        await using var f = new Fixture();
        using (var c = f.Db.Open())
        {
            c.Execute("INSERT INTO game (id, name_es) VALUES (7, 'Pokémon Esmeralda')");
            // Sin «asset», Save no consulta releases.
            void Add(string id, string consoles, string recommended = "") => c.Execute("INSERT INTO recipe (id, kind, json) VALUES (@id, 'emulator', @json)", new
            {
                id, json = $$"""{ "id": "{{id}}", "name": "{{id}}", "kind": "emulator", "folder": "x", "detect": "x.exe", "consoles": [{{consoles}}]{{recommended}} }""",
            });
            Add("mgba", "\"gba\"", """, "recommendedFor": [{ "game": "Pokémon Esmeralda", "gameId": 7, "source": "https://a" }, { "game": "Otro", "gameId": 8 }]""");
            Add("vba", "\"gba\"");
            Add("melonds", "\"ds\"", """, "recommendedFor": [{ "game": "Pokémon Esmeralda", "gameId": 7 }]""");
        }
        List<(string, string?)> Of(long game) => Recipes.ForGame(f.Db, game).Select(r => (r.Id, r.Source)).ToList();

        Assert.Null(await Recipes.Recommend(f.Db, 7, "gba", "vba", " https://b ", token: null));
        Assert.Equal([("melonds", null), ("vba", "https://b")], Of(7));
        Assert.Equal([("mgba", null)], Of(8)); // los otros juegos de la receta anterior siguen
        Assert.Contains("\"recommendedFor\"", Recipes.Get(f.Db, "mgba"));

        Assert.Equal("recipes.notForConsole", (await Recipes.Recommend(f.Db, 7, "ds", "vba", null, token: null))!.Code);
        Assert.Equal("recipes.noGame", (await Recipes.Recommend(f.Db, 99, "gba", "vba", null, token: null))!.Code);

        // «Quitar» lo saca de esa consola y deja la otra; sin juegos, la receta queda sin «recommendedFor».
        Assert.Null(await Recipes.Recommend(f.Db, 7, "gba", null, null, token: null));
        Assert.Equal([("melonds", null)], Of(7));
        Assert.DoesNotContain("recommendedFor", Recipes.Get(f.Db, "vba"));
    }

    [Fact]
    public async Task ARepoWithoutReleasesIsRejected()
    {
        await using var f = new Fixture();
        Recipes.Http = new HttpClient(new FakeReleases([]));
        Assert.Equal("recipes.noRelease", (await Recipes.Save(f.Db, "mgba", Recipe("x"), create: true, token: null))!.Code);
    }

    /// <summary>Las de main conservan sus bytes: guardarlas sin cambios no las muestra como cambiadas al publicar.</summary>
    [Fact]
    public async Task ImportedRecipesKeepTheirBytes()
    {
        await using var f = new Fixture();
        var main = Directory.CreateDirectory(Path.Combine(f.Tmp["main"], Published.RecipesFolder)).FullName;
        var text = "{\n  \"id\": \"dusklight-texturas\", \"name\": \"Texturas\", \"kind\": \"complement\",\n  \"folder\": \"Dusklight\", \"detect\": \"data\"\n}\n";
        File.WriteAllText(Path.Combine(main, "dusklight-texturas.json"), text);
        File.WriteAllText(Path.Combine(main, "receta.schema.json"), "{}");
        Assert.Equal(1, Recipes.Import(f.Db, main));
        Assert.Equal(0, Recipes.Import(f.Db, main));

        Assert.Null(await Recipes.Save(f.Db, "dusklight-texturas", text.Replace("\n", " "), create: false, token: null));
        Assert.Equal(text, Recipes.Get(f.Db, "dusklight-texturas"));
        Assert.Null(await Recipes.Save(f.Db, "dusklight-texturas", text.Replace("\"data\"", "\"data/x\""), create: false, token: null));
        Assert.Contains("\"detect\": \"data/x\"", Recipes.Get(f.Db, "dusklight-texturas"));
        Assert.Equal("recipes.idChanged", (await Recipes.Save(f.Db, "otra", text, create: false, token: null))!.Code);
    }

    [Fact]
    public async Task ADraftComesFromTheRepo()
    {
        Recipes.Http = new HttpClient(new FakeReleases(new() { [Releases] = MgbaReleases }));
        var (json, error) = await Recipes.Draft("github.com/mgba-emu/mgba/releases", token: null);
        Assert.Null(error);
        Assert.True(json!.Contains("\"asset\": \"^mGBA-[0-9.]+-win64\\\\.7z$\""), json);
        Assert.Contains("\"repo\": \"mgba-emu/mgba\"", json);
        Assert.Contains("\"id\": \"mgba\"", json);

        Assert.Equal("recipes.badRepo", (await Recipes.Draft("no es un repo", token: null)).Error!.Code);
        Assert.Equal(("forgejo", "eden-emu/eden", "git.eden-emu.dev"), Recipes.Source("https://git.eden-emu.dev/eden-emu/eden.git"));
    }

    /// <summary>#110: el patrón de un archivo calza con la versión siguiente, y el repo lista sus releases con archivos para elegir.</summary>
    [Fact]
    public async Task ThePatternMatchesTheNextVersionAndReleasesListTheirFiles()
    {
        var pattern = Recipes.AssetPattern("flycast-win64-2.4.zip");
        Assert.Matches(pattern, "flycast-win64-2.5.zip");
        Assert.Matches(pattern, "flycast-win64-2.10.1.zip");
        Assert.DoesNotMatch(pattern, "flycast-linux-2.5.zip");

        Recipes.Http = new HttpClient(new FakeReleases(new() { [Releases] = MgbaReleases }));
        var (list, error) = await Recipes.RepoReleases("github.com/mgba-emu/mgba", token: null);
        Assert.Null(error);
        var release = Assert.Single(list!);
        Assert.Equal([false, true], release.Files.Select(f => f.Windows));
        var (json, _) = await Recipes.Draft("github.com/mgba-emu/mgba", token: null, chosen: "mGBA-0.10.5-macos.dmg");
        Assert.Contains("macos", json);
    }

    /// <summary>#108: la tarjeta sabe la última release, si el patrón calza con ella y si la receta está publicada; la release se guarda un rato.</summary>
    [Fact]
    public async Task TheStatusComparesTheLatestReleaseAndMain()
    {
        await using var f = new Fixture();
        var fake = new FakeReleases(new() { [Releases] = MgbaReleases });
        Recipes.Http = new HttpClient(fake);
        Assert.Null(await Recipes.Save(f.Db, "mgba", Recipe("^mGBA-[0-9.]+-win64\\\\.7z$"), create: true, token: null));
        var main = Directory.CreateDirectory(Path.Combine(f.Tmp["main"], Published.RecipesFolder)).FullName;
        var none = f.Tmp["sin-otteretro"];

        Assert.Equal(new RecipeStatus("mgba", "0.10.5", "2026-01-01", true, "changed"), Assert.Single(await Recipes.Status(f.Db, main, token: null, local: none)));
        File.WriteAllText(Path.Combine(main, "mgba.json"), Recipes.Get(f.Db, "mgba")!.Replace("\n", " "));
        Assert.Equal("published", Assert.Single(await Recipes.Status(f.Db, main, token: null, local: none)).State);
        Assert.Null(Assert.Single(await Recipes.Status(f.Db, null, token: null, local: none)).State);
        Assert.Equal(2, fake.Calls.Count); // la de Save y una de Status: las otras dos la tomaron de lo guardado

        // Una release nueva sin el archivo de siempre: el patrón calza con una vieja (se puede guardar), pero no con la última.
        Recipes.Http = new HttpClient(new FakeReleases(new() { [Releases] = """
            [
              { "tag_name": "0.11", "draft": false, "published_at": "2026-10-01", "assets": [{ "name": "mGBA-0.11-win64.zip" }] },
              { "tag_name": "0.10.5", "draft": false, "published_at": "2026-01-01", "assets": [{ "name": "mGBA-0.10.5-win64.7z" }] }
            ]
            """ }));
        Assert.Equal(new RecipeStatus("mgba", "0.11", "2026-10-01", false, "published"), Assert.Single(await Recipes.Status(f.Db, main, token: null, local: none)));
    }

    /// <summary>#109: probar la descarga baja la última release que calza, informa y no deja nada en el disco; descartar vuelve a main o borra.</summary>
    [Fact]
    public async Task TestDownloadsAndCleansUpAndDiscardRestores()
    {
        await using var f = new Fixture();
        const string url = "https://example.test/mGBA-0.10.5-win64.zip";
        var zip = new MemoryStream();
        using (var a = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, true))
            a.CreateEntry("mGBA-0.10.5/mGBA.exe");
        var bodies = new Dictionary<string, string> { [Releases] = $$"""[{ "tag_name": "0.10.5", "published_at": "2026-01-01", "assets": [{ "name": "mGBA-0.10.5-win64.zip", "browser_download_url": "{{url}}" }] }]""" };
        Recipes.Http = new HttpClient(new FakeReleases(bodies, new() { [url] = zip.ToArray() }));
        Assert.Null(await Recipes.Save(f.Db, "mgba", Recipe("^mGBA-[0-9.]+-win64\\\\.zip$"), create: true, token: null));
        var before = Directory.GetDirectories(Path.GetTempPath(), "otteretro-catalogo-test-*").Length;

        var (result, error) = await Recipes.Test(f.Db, "mgba", token: null);
        Assert.Null(error);
        Assert.Equal(("0.10.5", "mGBA-0.10.5-win64.zip", true), (result!.Tag, result.File, result.Found));
        Assert.True(result.Size > 0);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "otteretro-catalogo-test-*").Length);

        // Sin main que leer no se descarta; con main sin la receta, se borra.
        Assert.False(Recipes.Discard(f.Db, "mgba", null));
        var main = Directory.CreateDirectory(Path.Combine(f.Tmp["main2"], Published.RecipesFolder)).FullName;
        Assert.True(Recipes.Discard(f.Db, "mgba", main));
        Assert.Empty(Recipes.List(f.Db));
    }
}

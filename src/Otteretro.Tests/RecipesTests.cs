using System.Net;
using System.Text;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>#95: «Emuladores» guarda una receta solo si cumple el esquema y su fuente tiene una release con un archivo que calce.</summary>
public class RecipesTests
{
    /// <summary>Releases falsas por URL pedida; lo que no está es un 404.</summary>
    sealed class FakeReleases(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add(url);
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
}

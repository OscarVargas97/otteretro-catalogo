using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Otteretro.Api;
using Otteretro.App;

namespace Otteretro.Tests;

public class LocalServerTests : IDisposable
{
    readonly TempDir tmp = new();
    readonly WebApplication app;
    readonly HttpClient client;
    readonly AppSession session;

    public LocalServerTests()
    {
        session = new AppSession(new AppConfig(), tmp["config.json"], LocalServer.NewKey());
        app = LocalServer.Create(session, b => b.WebHost.UseTestServer());
        app.StartAsync().GetAwaiter().GetResult();
        client = app.GetTestClient();
        client.BaseAddress = new Uri("http://127.0.0.1/");
    }

    [Fact]
    public async Task WithoutKeyReturns401()
    {
        var r = await client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task WrongKeyReturns401()
    {
        client.DefaultRequestHeaders.Add(LocalServer.KeyHeader, "otra");
        var r = await client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task WithKeyResponds()
    {
        client.DefaultRequestHeaders.Add(LocalServer.KeyHeader, session.Key);
        var state = await client.GetFromJsonAsync<AppState>("/api/state");
        Assert.NotNull(state);
        Assert.False(state.Configured);
    }

    [Fact]
    public async Task KeyInTheUrlAlsoWorks()
    {
        var r = await client.GetAsync($"/api/state?key={session.Key}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task ForeignHostReturns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Host = "pagina-maliciosa.com";
        request.Headers.Add(LocalServer.KeyHeader, session.Key);
        var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ChoosingTheDataFolderPreparesAndSavesIt()
    {
        client.DefaultRequestHeaders.Add(LocalServer.KeyHeader, session.Key);
        var folder = tmp["datos"];
        var r = await client.PostAsJsonAsync("/api/data-folder", new FolderRequest(folder));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(DataFolder.IsReady(folder));
        Assert.True(File.Exists(DataFolder.DatabasePath(folder)));
        Assert.Equal(folder, AppConfig.Load(tmp["config.json"]).DataFolder);
    }

    [Fact]
    public async Task InvalidFolderReturns400()
    {
        client.DefaultRequestHeaders.Add(LocalServer.KeyHeader, session.Key);
        var r = await client.PostAsJsonAsync("/api/data-folder", new FolderRequest("relativa"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // El backend no manda textos: el front traduce el código (#142).
        Assert.Equal("path.notAbsolute", (await r.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task ChangingTheLanguageIsSaved()
    {
        client.DefaultRequestHeaders.Add(LocalServer.KeyHeader, session.Key);
        var r = await client.PutAsJsonAsync("/api/language", new LanguageRequest("en"));
        Assert.Equal("en", (await r.Content.ReadFromJsonAsync<AppState>())!.Language);
        Assert.Equal("en", AppConfig.Load(tmp["config.json"]).Language);

        var bad = await client.PutAsJsonAsync("/api/language", new LanguageRequest("xx"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public void FindsThePrefetchFileWhereTheExportWritesIt()
    {
        var root = tmp["wwwroot"];
        Directory.CreateDirectory(Path.Combine(root, "sagas", "__next.sagas"));
        File.WriteAllText(Path.Combine(root, "sagas", "__next.sagas", "__PAGE__.txt"), "x");
        File.WriteAllText(Path.Combine(root, "__next.__PAGE__.txt"), "x");

        Assert.Equal("/sagas/__next.sagas/__PAGE__.txt", LocalServer.FindPrefetchFile(root, "/sagas/__next.sagas.__PAGE__.txt"));
        Assert.Null(LocalServer.FindPrefetchFile(root, "/__next.__PAGE__.txt"));        // existe tal cual
        Assert.Null(LocalServer.FindPrefetchFile(root, "/steam/__next.steam.__PAGE__.txt")); // no existe de ninguna forma
        Assert.Null(LocalServer.FindPrefetchFile(root, "/sagas/index.txt"));              // no es de precarga
    }

    public void Dispose()
    {
        app.StopAsync().GetAwaiter().GetResult();
        ((IDisposable)app).Dispose();
        FileLog.Folder = Path.Combine(Path.GetTempPath(), "otteretro-pruebas", "logs");
        tmp.Dispose();
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Otteretro.Catalog;

namespace Otteretro.Tests;

/// <summary>otteretro-catalogo#174: qué trae el archivo de una release, leído con pedidos Range sin bajarlo entero, y el ejecutable que se elige.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparte Recipes.Http
public class AssetFilesTests : IDisposable
{
    readonly HttpClient realHttp = Recipes.Http;
    readonly Server server = new();

    public AssetFilesTests() => Recipes.Http = new HttpClient(server);
    public void Dispose() => Recipes.Http = realHttp;

    /// <summary>Sirve archivos con Range (o enteros, sin <see cref="Ranges"/>) y cuenta los bytes que manda.</summary>
    sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public bool Ranges = true;
        public long Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (!Ranges || request.Headers.Range?.Ranges.Single() is not { } r) { Sent += file.Length; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) }); }
            var from = r.From ?? Math.Max(0, file.Length - r.To!.Value);
            var to = r.From is null ? file.Length - 1 : Math.Min(r.To!.Value, file.Length - 1);
            var part = file[(int)from..(int)(to + 1)];
            Sent += part.Length;
            var content = new ByteArrayContent(part);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    /// <summary>Un zip con esas rutas; cada archivo con relleno que no se comprime, para que el zip sea grande.</summary>
    static byte[] Zip(params string[] names)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create))
            foreach (var name in names)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                if (!name.EndsWith('/')) s.Write(new byte[200_000]);
            }
        return ms.ToArray();
    }

    async Task<List<string>?> List(string name, byte[] file)
    {
        var url = "https://dl/" + name;
        server.Files[url] = file;
        return await AssetFiles.List(name, url, CancellationToken.None);
    }

    [Fact]
    public async Task AZipWithTheExeIsReadByItsDirectoryWithoutDownloadingIt()
    {
        var zip = Zip("Game.exe", "vc_redist.x64.exe", "data/level.pak");
        var files = await List("game-win64-1.0.zip", zip);
        Assert.Equal(["Game.exe", "vc_redist.x64.exe", "data/level.pak"], files);
        Assert.Equal("Game.exe", AssetFiles.Pick(files!, "other")); // el instalador no cuenta
        Assert.True(AssetFiles.Has(files!, "Game.exe"));
        Assert.True(server.Sent < zip.Length / 4, $"{server.Sent} de {zip.Length}");

        // Un servidor sin Range: un zip chico se baja entero y sirve igual.
        server.Ranges = false;
        Assert.Equal(files, await List("game-win64-1.0.zip", zip));
    }

    [Fact]
    public async Task AZipWithOneFolderIsReadFromInsideIt()
    {
        var files = await List("game.zip", Zip("Game-1.0/", "Game-1.0/Game.exe", "Game-1.0/Tool.exe", "Game-1.0/bin/x.dll"));
        Assert.Equal(["Game.exe", "Tool.exe", "bin/x.dll"], files);
        Assert.Null(AssetFiles.Pick(files!, "other")); // dos sin uno claro
        Assert.Equal("Game.exe", AssetFiles.Pick(files!, "game")); // entre varios, el que se llama como el repo
        Assert.False(AssetFiles.Has(files!, "Game-1.0/Game.exe"));
    }

    [Fact]
    public async Task AZipWithoutAnExeHasNoneToPick()
    {
        // Como el NX2navi-app: la app en Python, sin el lanzador.
        var files = await List("NX2navi-app-1.0.zip", Zip("NX2navi/main.py", "NX2navi/assets/a.png", "README.md"));
        Assert.Empty(AssetFiles.Exes(files!));
        Assert.Null(AssetFiles.Pick(files!, "NX2navi"));
        Assert.False(AssetFiles.Has(files!, "NX2navi.exe"));
    }

    [Fact]
    public async Task AnExeAssetIsItselfAndOtherFormatsCannotBeKnown()
    {
        Assert.Equal(["NX2navi-Setup-1.0.exe"], await AssetFiles.List("NX2navi-Setup-1.0.exe", "https://dl/no", CancellationToken.None));
        Assert.Equal(["SRM.exe"], await AssetFiles.List("SRM-2.5.exe", "https://dl/no", CancellationToken.None, saveAs: "SRM.exe"));
        Assert.Null(await AssetFiles.List("mGBA-0.11-win64.7z", "https://dl/no", CancellationToken.None));
        Assert.Equal(0, server.Sent);
        Assert.Null(await List("roto.zip", new byte[100]));
    }
}

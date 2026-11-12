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

    /// <summary>Sirve archivos con Range (o enteros, sin <see cref="Ranges"/>) y cuenta los bytes que manda. Un rango del final da 501, como GitHub.</summary>
    sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public bool Ranges = true;
        public long Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (!Ranges || request.Headers.Range?.Ranges.Single() is not { } r) { Sent += file.Length; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) }); }
            if (r.From is not { } from) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented)); // como el CDN de GitHub con bytes=-N
            var to = Math.Min(r.To!.Value, file.Length - 1);
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

    /// <summary>otteretro-catalogo#179: un caso de docs/reglas-empaquetado.md por línea, con nombres sacados de releases reales.</summary>
    [Theory]
    [InlineData("TotK-Setup.exe|totk-pc-recomp.exe", "totk-pc-recomp", "totk-pc-recomp.exe")] // instalador con otro nombre al lado
    [InlineData("Astral Chain-Setup.exe", "astral-chain-pc-recomp", null)] // solo el instalador: no hay juego que elegir
    [InlineData("azahar.exe|azahar-room.exe", "azahar", "azahar.exe")] // servidor de salas
    [InlineData("openMERC.exe|openMerc-ded.exe", "openmerc", "openMERC.exe")] // servidor dedicado
    [InlineData("eden.exe|eden-cli.exe", "eden", "eden.exe")] // de consola
    [InlineData("KameoRePowered.exe|KameoBugReporter.exe|KameoCrashDialog.exe|KameoShaderEditor.exe", "x", "KameoRePowered.exe")] // ayudantes
    [InlineData("condemned2recomp.exe|condemned2recomp_updater.exe|UnityCrashHandler64.exe", "x", "condemned2recomp.exe")]
    [InlineData("RampageRE.exe|RampageRE-dev.exe|strikers-settings.exe|geow2-extract.exe", "x", "RampageRE.exe")]
    [InlineData("Play RatchetPort.exe|RatchetPort.exe", "RatchetPort", "RatchetPort.exe")] // juego y lanzador: el que se llama como el repo
    [InlineData("Play RatchetPort.exe|RatchetPort.exe", "otro", "RatchetPort.exe")] // el lanzador abre el mismo juego
    [InlineData("Bloodborne.exe|BLauncher.exe|bin/bb-probe.exe|bin/bb-gpu-capabilities.exe", "bloodborne-pc", "Bloodborne.exe")]
    [InlineData("x/topgear.exe|x/topgear-launcher.exe|leeme.txt", "topgear-pc", "x/topgear.exe")] // sin .exe en la raíz: las subcarpetas
    [InlineData("kyty_launcher.exe|kyty_emulator.exe", "kytyps5-fork", "kyty_launcher.exe|emu")] // en un emulador, el lanzador es su interfaz
    [InlineData("Alundra.exe|Alundra_Recompiled.exe", "alundra-recomp", null)] // sin uno claro: lo elige la persona
    [InlineData("quake3e.x64.exe|quake3e.ded.x64.exe|quake3e-vulkan.x64.exe", "x", null)] // variantes: lo elige la persona
    [InlineData("Installer.exe|payload/FFTARecomp.exe", "FFTARecomp", null)] // lo de adentro es del instalador
    [InlineData("Jazz2.exe|Jazz2_sdl2.exe", "jazz2", "Jazz2.exe")]
    [InlineData("KameoRePowered.exe|KameoLauncher.exe", "Kameo-RePowered", "KameoRePowered.exe")] // sin contar guiones ni mayúsculas
    [InlineData("bin/warzone2100.exe|bin/tool.exe", "warzone2100", "bin/warzone2100.exe")] // en una subcarpeta, el del repo
    [InlineData("bin/nx2navi_xlate.exe", "NX2navi", null)] // en una subcarpeta y con otro nombre: no se adivina
    [InlineData("game.exe|bin/game.exe", "game", "game.exe")] // la raíz manda
    public void PickSkipsHelpersAndPrefersTheRepoName(string files, string repo, string? exe)
    {
        var emulator = exe?.EndsWith("|emu") == true;
        Assert.Equal(emulator ? null : exe, AssetFiles.Pick([.. files.Split('|')], repo, emulator));
    }

    [Theory]
    [InlineData("Game-Setup-1.0.exe", "Inno Setup Setup Data (6.2.0)", "inno")]
    [InlineData("Game-Installer.exe", "NullsoftInst", "nsis")]
    [InlineData("game.exe", "7z¼¯'\u001c", "7zsfx")]
    [InlineData("Nebula-Setup.exe", "PK\u0005\u0006", "setup")] // un instalador propio (como el de NX2navi): solo lo dice el nombre
    [InlineData("game-1.2.exe", "nada", null)] // el juego mismo
    public async Task PackerTellsAnInstallerByItsSignature(string name, string mark, string? packer)
    {
        var url = "https://dl/" + name;
        server.Files[url] = [.. new byte[4096], .. System.Text.Encoding.Latin1.GetBytes(mark), .. new byte[4096]];
        Assert.Equal(packer, await AssetFiles.Packer(name, url, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAppliesTheRules()
    {
        server.Files["https://dl/G-Setup-1.0.exe"] = [.. "Inno Setup"u8];
        var inno = await AssetFiles.Resolve("G-Setup-1.0.exe", "https://dl/G-Setup-1.0.exe", "g", false, CancellationToken.None);
        Assert.Equal(("inno", "/DIR={dir}"), (inno.Packer, inno.Installer![^1]));
        Assert.Null(inno.Exe); // lo que deja no se sabe sin correrlo

        server.Files["https://dl/PandorasTower-PC-v1.2.exe"] = new byte[100];
        var loose = await AssetFiles.Resolve("PandorasTower-PC-v1.2.exe", "https://dl/PandorasTower-PC-v1.2.exe", "pandora-tower-pc", false, CancellationToken.None);
        Assert.Equal(("pandora-tower-pc.exe", "pandora-tower-pc.exe"), (loose.Exe, loose.SaveAs)); // la versión fuera del nombre

        var zip = await AssetFiles.Resolve("x.zip", "https://dl/no-existe.zip", "x", false, CancellationToken.None);
        Assert.Equal(new AssetFiles.Resolved(null, null, null, null, null), zip); // un 404: no se puede saber
    }
}

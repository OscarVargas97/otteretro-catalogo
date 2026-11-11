using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.App;
using Otteretro.Catalog;
using Otteretro.Claude;

namespace Otteretro.Tests;

/// <summary>#69 a #72 sin red: la lista de ports, su cruce con los juegos, la compatibilidad, la búsqueda de nuevos y lo publicado.</summary>
[Collection(nameof(ClaudeRunnerTests))] // comparten ClaudeRunner.Launch, ClaudeBudget.UtcNow y Ports.Handler
public class PortsTests : IAsyncDisposable
{
    readonly Fixture f = new();
    readonly List<string> calls = [];
    readonly Func<ProcessStartInfo, string, CancellationToken, Task<ProcessOutput>> real = ClaudeRunner.Launch;
    readonly Func<DateTime> realNow = ClaudeBudget.UtcNow;
    readonly HttpClient realHttp = Recipes.Http;

    /// <summary>Lo que responde la red: el tar de Recompendium, las listas de compatibilidad, el RSS y Read Only Memo.</summary>
    sealed class Net(Dictionary<string, string> yaml) : HttpMessageHandler
    {
        public string Rss = "", Memo = "";
        /// <summary>Las releases de cada repo de GitHub («dueño/repo» → JSON); las que faltan, 404.</summary>
        public readonly Dictionary<string, string> Releases = [];
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (url.Contains("/releases")) Asked.Add(url);
            if (Releases.FirstOrDefault(r => url.Contains($"api.github.com/repos/{r.Key}/releases")).Value is { } release)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release, Encoding.UTF8) });
            if (url.Contains("codeload.github.com")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tar(yaml)) });
            string? body = url switch
            {
                _ when url.Contains("dolphin-emu") && url.Contains("Category:5_stars") => """{"query":{"categorymembers":[{"title":"Metroid Prime (GC)"}]}}""",
                _ when url.Contains("dolphin-emu") => """{"query":{"categorymembers":[]}}""",
                _ when url.Contains("api.github.com/search/issues") && url.Contains("Banjo-Kazooie") =>
                    """{"items":[{"title":"4D5307ED - Banjo-Kazooie: Nuts & Bolts","labels":[{"name":"state-playable"}]}]}""",
                _ when url.Contains("api.github.com/search/issues") => """{"items":[]}""",
                _ when url.Contains("rss.xml") => Rss,
                _ when url.Contains("readonlymemo") => Memo,
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }

        static byte[] Tar(Dictionary<string, string> files)
        {
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gz))
                foreach (var (path, text) in files)
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"unricopie-abc/src/data/{path}") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            return output.ToArray();
        }
    }

    readonly Dictionary<string, string> yaml = new()
    {
        // Mismo enlace que un extra: queda el de Recompendium.
        ["recomps/zelda-majoras-mask.yaml"] = "name: \"The Legend of Zelda: Majora's Mask\"\nconsole: n64\nstatus: fully\nrepo: Zelda64Recomp/Zelda64Recomp\ntoolchain: N64Recomp\ndesc:\n  es: \"x\"\n",
        // En un port, `name` es el proyecto: el juego sale del mapa de `catalogo.mjs`.
        ["ports/2ship2harkinian.yaml"] = "name: 2 Ship 2 Harkinian\ncategory: decomp-port\nconsole: n64\nstatus: fully\nrepo: HarbourMasters/2ship2harkinian\n",
        ["recomps/metroid-prime.yaml"] = "name: Metroid Prime\nconsole: gamecube\nstatus: playable\nrepo: a/prime-recomp\ntoolchain: Wiicompiled\n",
        ["recomps/banjo.yaml"] = "name: \"Banjo-Kazooie: Nuts & Bolts\"\nconsole: x360\nstatus: experimental\nrepo: b/nb-recomp\ntoolchain: ReXGlue\n",
        ["recomps/rush.yaml"] = "name: Rush 2049\nconsole: virtualboy\nstatus: beta\nabandoned: true\nrepo: c/rush\n", // Virtual Boy es «vb» en el catálogo
        ["recomps/doom-dos.yaml"] = "name: Doom\nconsole: dos\nstatus: playable\nrepo: d/doom-dos\n", // DOS no es una consola del catálogo: no entra (#155)
        ["decomps/sm64.yaml"] = "name: Super Mario 64\nconsole: n64\nrepo: n64decomp/sm64\n", // entra como «sin port todavía»
        ["decomps/sin-consola.yaml"] = "name: Algo\nrepo: x/algo\n", // sin consola no entra
    };

    readonly Net net;

    public PortsTests()
    {
        net = new Net(yaml);
        Ports.Handler = net;
        Recipes.Http = new HttpClient(net); // las releases de las recetas propuestas (#119)
        ClaudeBudget.UtcNow = () => TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(3), TimeZoneInfo.Local); // de noche
        using var c = f.Db.Open();
        c.Execute("""
            INSERT INTO catalog.saga (id, name) VALUES (1, 'The Legend of Zelda');
            INSERT INTO catalog.game (id, saga_id, name_es, original_name) VALUES (10, 1, 'The Legend of Zelda: Majora''s Mask', NULL),
                (11, NULL, 'Metroid Prime', NULL), (12, NULL, 'Banjo-Kazooie: Baches y cachivaches', 'Banjo-Kazooie: Nuts & Bolts'),
                (13, NULL, 'Super Metroid', NULL), (14, NULL, 'Doom', NULL), (15, NULL, 'Doom', NULL), (17, NULL, 'Super Mario 64', NULL);
            INSERT INTO catalog.version (id, game_id, platform, kind) VALUES (20, 10, 'n64', 'original'), (21, 11, 'gc', 'original'),
                (22, 12, 'x360', 'original'), (23, 13, 'snes', 'original'), (24, 14, 'ps1', 'original'), (25, 15, 'ps1', 'original'), (27, 17, 'n64', 'original');
            """);
    }

    public async ValueTask DisposeAsync()
    {
        Ports.Handler = null;
        Recipes.Http = realHttp;
        ClaudeRunner.Launch = real;
        ClaudeBudget.UtcNow = realNow;
        await f.DisposeAsync();
    }

    T Scalar<T>(string sql)
    {
        using var c = f.Db.Open();
        return c.ExecuteScalar<T>(sql)!;
    }

    async Task Done(long id)
    {
        var task = await f.Run(id);
        Assert.True(task.Status == "done", $"{task.Status}: {JsonSerializer.Serialize(task.Detail)}");
    }

    long PortId(string project) => Scalar<long>($"SELECT id FROM port WHERE project = '{project}'");

    /// <summary>#70: la lista sale de Recompendium y los extras, con su compatibilidad; el cruce va aparte y deja visible lo que no cruzó.</summary>
    [Fact]
    public async Task TheListIsRefreshedAndMatchedWithTheGames()
    {
        await Done(Ports.Enqueue(f.Db));

        // 5 ports y 1 decompilación de Recompendium, y 20 extras (uno ya estaba; los de Mega Drive y arcade no son consolas del catálogo).
        Assert.Equal(26, Scalar<long>("SELECT count(*) FROM port"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM port WHERE console NOT IN (SELECT id FROM console)"));
        Assert.Equal("recompendium", Scalar<string>("SELECT source FROM port WHERE link = 'https://github.com/Zelda64Recomp/Zelda64Recomp'"));
        using (var c = f.Db.Open())
        {
            var matched = c.Query<(string, long)>("SELECT project, game_id FROM port WHERE game_id IS NOT NULL ORDER BY lower(project)").ToList();
            // Por nombre y consola, también por el nombre original; los dos «Doom» de PS1 no se adivinan.
            Assert.Equal([("2ship2harkinian", 10L), ("nb-recomp", 12L), ("prime-recomp", 11L), ("sm64", 17L), ("sm64coopdx (multijugador)", 17L), ("SM64Plus", 17L), ("Super Metroid Launcher", 13L), ("Zelda64Recomp", 10L)], matched);
            Assert.Equal(("decomp", "no-port"), c.QuerySingle<(string, string)>("SELECT kind, status FROM port WHERE project = 'sm64'"));
            var compat = c.Query<(string, string?, long?)>("SELECT project, emulator, emulator_level FROM port WHERE project IN ('prime-recomp', 'nb-recomp', 'Zelda64Recomp') ORDER BY lower(project)").ToList();
            Assert.Equal([("nb-recomp", "Xenia Canary", 2L), ("prime-recomp", "Dolphin", 3L), ("Zelda64Recomp", "ares / simple64", null)], compat);
            Assert.Equal(("recomp", "N64Recomp", "complete"), c.QuerySingle<(string, string, string)>("SELECT kind, toolchain, status FROM port WHERE project = 'Zelda64Recomp'"));
            Assert.Equal(("decomp", true), c.QuerySingle<(string, bool)>("SELECT kind, abandoned = 0 FROM port WHERE project = '2ship2harkinian'"));
        }
        var page = Ports.Page(f.Db);
        Assert.Equal((26, 8), (page.Total, page.Matched));
        Assert.Equal(Ports.Statuses, page.ByStatus.Keys); // «100 % jugable» primero, aparte de lo jugable con fallos
        Assert.Equal(1, page.ByStatus["no-port"]);
        Assert.Equal(Scalar<long>("SELECT count(*) FROM port WHERE status = 'complete'"), page.ByStatus["complete"]);
        Assert.Contains(page.Unmatched, p => p.Project == "PsyDoom");
        Assert.Contains(page.Unmatched, p => p.Console == "vb" && p.Project == "rush");

        // A mano: el cruce no lo cambia y, si el juego se fusiona, sigue al que lo reemplazó.
        Assert.True(Ports.Assign(f.Db, PortId("PsyDoom"), 14));
        Assert.False(Ports.Assign(f.Db, PortId("PsyDoom"), 999));
        using (var c = f.Db.Open())
            c.Execute("DELETE FROM version WHERE game_id = 14; DELETE FROM game WHERE id = 14; INSERT INTO replaced (kind, old_id, new_id) VALUES ('game', 14, 15)");
        Ports.Match(f.Db);
        Assert.Equal(15, Scalar<long>("SELECT game_id FROM port WHERE project = 'PsyDoom'"));

        // Actualizar: lo que Recompendium ya no tiene se conserva marcado «ya no listado»; lo demás y su cruce siguen. La decompilación
        // que ahora tiene port (mismo enlace) pasa a port con la fecha de hoy: es una novedad.
        yaml.Remove("recomps/rush.yaml");
        yaml.Remove("decomps/sm64.yaml");
        yaml["ports/sm64-port.yaml"] = "name: SM64 Port\ncategory: decomp-port\nconsole: n64\nstatus: playable\nrepo: n64decomp/sm64\n";
        using (var c = f.Db.Open())
            c.Execute("""
                UPDATE port SET added = '2020-01-01' WHERE project = 'sm64';
                INSERT INTO port (console, game, project, kind, status, link, source, added) VALUES ('mac', 'Marathon', 'Aleph One', 'engine', 'complete', 'https://x/aleph', 'readonlymemo', '2026-01-01')
                """);
        var refresh = Ports.Enqueue(f.Db);
        await Done(refresh);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM port WHERE console = 'mac'")); // de antes, de una consola que no está y sin receta: se borra
        using (var c = f.Db.Open()) // el cambio de estado queda en el registro
            Assert.Contains(c.Query<string>("SELECT message FROM task_log WHERE task_id = @refresh", new { refresh }), m => m.Contains("ports.log.status") && m.Contains("playable"));
        Assert.Equal(1, Scalar<long>("SELECT delisted FROM port WHERE project = 'rush'"));
        Assert.Equal(9, Ports.Page(f.Db).Matched);
        Assert.Equal(15, Scalar<long>("SELECT game_id FROM port WHERE project = 'PsyDoom'"));
        using (var c = f.Db.Open())
            Assert.Equal(("playable", Ports.Today(), 17L), c.QuerySingle<(string, string, long)>("SELECT status, added, game_id FROM port WHERE link = 'https://github.com/n64decomp/sm64'"));

        // Si vuelve a aparecer, deja de estar marcado.
        yaml["recomps/rush.yaml"] = "name: Rush 2049\nconsole: virtualboy\nstatus: beta\nrepo: c/rush\n";
        await Done(Ports.Enqueue(f.Db));
        Assert.Equal(0, Scalar<long>("SELECT delisted FROM port WHERE project = 'rush'"));
    }

    static string Release(string tag, params string[] files) =>
        $$"""[{ "tag_name": "{{tag}}", "draft": false, "published_at": "2026-10-01T00:00:00Z", "assets": [{{string.Join(",", files.Select(a => $$"""{ "name": "{{a}}", "browser_download_url": "https://dl/{{a}}" }"""))}}] }]""";

    /// <summary>
    /// #119, otteretro-catalogo#153: después del cruce, cada port con juego y una release tiene su receta, sin pasar por Revisar, con el
    /// archivo de cada sistema (<c>asset</c> de Windows, <c>assetLinux</c> de Linux); el de solo Linux también. Si de un sistema hay varios
    /// archivos sin uno claro, la receta se crea igual con el aviso «Elegir el archivo» en su tarjeta. Lo que ya tiene receta no se pide, lo
    /// que no tiene archivo no se repite hasta que cambie su release, y lo que esperaba en Revisar de antes se convierte en receta.
    /// </summary>
    [Fact]
    public async Task PortsWithAReleaseGetTheirRecipeWithAFilePerSystem()
    {
        net.Releases["Zelda64Recomp/Zelda64Recomp"] = Release("v1.2.0", "Zelda64Recompiled-1.2.0-Windows.zip", "Zelda64Recompiled-1.2.0-Linux.tar.gz");
        net.Releases["a/prime-recomp"] = Release("v0.3", "prime-0.3-win64.zip", "prime-0.3-win32.zip", "prime-0.3-win64-debug.zip");
        net.Releases["b/nb-recomp"] = Release("v1", "nb-source.tar.gz");
        net.Releases["HarbourMasters/2ship2harkinian"] = Release("v2", "2ship-win64.zip");
        using (var c = f.Db.Open())
            c.Execute("""INSERT INTO recipe (id, kind, json) VALUES ('2ship', 'port', '{ "id": "2ship", "updates": { "source": "github", "repo": "HarbourMasters/2ship2harkinian" } }')""");

        await Done(Ports.Enqueue(f.Db));
        Assert.DoesNotContain(net.Asked, u => u.Contains("2ship2harkinian")); // ya tiene receta
        var zelda = System.Text.Json.Nodes.JsonNode.Parse(Recipes.Get(f.Db, "zelda64recomp")!)!;
        Assert.Equal(@"^Zelda64Recompiled-[0-9.]+-Windows\.zip$", (string)zelda["asset"]!);
        Assert.Equal(@"^Zelda64Recompiled-[0-9.]+-Linux\.tar\.gz$", (string)zelda["assetLinux"]!);
        var recipe = System.Text.Json.Nodes.JsonNode.Parse(Recipes.Get(f.Db, "prime-recomp")!)!;
        Assert.Equal(("port", "GameCube", "Metroid Prime", true, "a/prime-recomp", "https://github.com/a/prime-recomp"),
            ((string)recipe["kind"]!, (string)recipe["folder"]!, (string)recipe["game"]!, (bool)recipe["experimental"]!, (string)recipe["updates"]!["repo"]!, (string)recipe["port"]!));
        Assert.Matches((string)recipe["asset"]!, "prime-0.3-win64.zip"); // de varios, el de 64 bits que no es de depuración
        Assert.Null(Recipes.Get(f.Db, "nb-recomp")); // sin archivo de Windows ni de Linux

        // Otra vez: lo creado no se vuelve a pedir; el que no tenía archivo se mira de nuevo. Solo Linux, con dos archivos sin uno claro: la
        // receta se crea con el aviso «Elegir el archivo» y los archivos de la release.
        net.Asked.Clear();
        await Done(Ports.Enqueue(f.Db));
        Assert.Contains(net.Asked, u => u.Contains("b/nb-recomp"));
        Assert.DoesNotContain(net.Asked, u => u.Contains("prime-recomp") || u.Contains("Zelda64Recomp"));
        net.Releases["b/nb-recomp"] = Release("v2", "nb-linux-gl.tar.gz", "nb-linux-vk.tar.gz");
        await Done(Ports.Enqueue(f.Db));
        var nb = System.Text.Json.Nodes.JsonNode.Parse(Recipes.Get(f.Db, "nb-recomp")!)!;
        Assert.Null(nb["asset"]);
        Assert.Null(nb["assetLinux"]);
        var notice = Assert.Single(EmulatorReview.Notices(f.Db));
        Assert.Equal(("nb-recomp", "pickFile", 2), (notice.Recipe, notice.Reason, notice.Files.Count));
        Assert.Equal(notice.Id, Assert.Single(Recipes.List(f.Db).Single(r => r.Id == "nb-recomp").Notices!).Id);
        Assert.True(EmulatorReview.Dismiss(f.Db, notice.Id));
        Assert.Empty(EmulatorReview.Notices(f.Db));

        // Lo que esperaba en Revisar de antes se convierte en receta en la próxima vuelta.
        using (var c = f.Db.Open())
        {
            c.Execute("DELETE FROM recipe WHERE id = 'zelda64recomp'");
            c.Execute("""
                INSERT INTO review_item (kind, entity, entity_id, data, created_at) SELECT 'doubtful', 'port', id, json_object('port', id, 'game', game,
                'project', project, 'console', console, 'folder', 'Nintendo 64', 'source', 'github', 'repo', 'Zelda64Recomp/Zelda64Recomp',
                'url', link, 'tag', 'v1.2.0', 'files', json_array('Zelda64Recompiled-1.2.0-Windows.zip'), 'link', link), '2026-01-01' FROM port WHERE project = 'Zelda64Recomp'
                """);
        }
        await Done(Ports.Enqueue(f.Db));
        Assert.NotNull(Recipes.Get(f.Db, "zelda64recomp"));
        using (var c = f.Db.Open()) Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM review_item WHERE entity = 'port' AND status = 'open'"));
    }

    /// <summary>#149: la receta de un port apunta a su fila por el enlace, que le da su consola, su carpeta y su juego; un enlace repetido no tumba la tarea.</summary>
    [Fact]
    public async Task APortRecipePointsToItsPortRow()
    {
        net.Releases["a/prime-recomp"] = Release("v0.3", "prime-0.3-win64.zip");
        await Done(Ports.Enqueue(f.Db));
        Assert.Equal("https://github.com/a/prime-recomp", (string)System.Text.Json.Nodes.JsonNode.Parse(Recipes.Get(f.Db, "prime-recomp")!)!["port"]!);

        // A mano: sin «port» o con un enlace que no es un port no se guarda; la carpeta y el juego escritos a mano los pisa la fila.
        const string recipe = """{ "id": "prime", "name": "Prime", "kind": "port", "folder": "Otra", "game": "Otro", "detect": "prime.exe"PORT }""";
        Assert.Equal("recipes.noPort", (await Recipes.Save(f.Db, "prime", recipe.Replace("PORT", ""), true, null))?.Code);
        Assert.Equal("recipes.portUnknown", (await Recipes.Save(f.Db, "prime", recipe.Replace("PORT", @", ""port"": ""https://github.com/x/y"""), true, null))?.Code);
        Assert.Null(await Recipes.Save(f.Db, "prime", recipe.Replace("PORT", @", ""port"": ""https://github.com/a/prime-recomp/"""), true, null));
        var saved = System.Text.Json.Nodes.JsonNode.Parse(Recipes.Get(f.Db, "prime")!)!;
        Assert.Equal(("GameCube", "Metroid Prime"), ((string)saved["folder"]!, (string)saved["game"]!));
        var info = Recipes.List(f.Db, "").Single(r => r.Id == "prime");
        Assert.Equal(("gc", "Metroid Prime"), (Assert.Single(info.Consoles), info.Game)); // «Emuladores» la agrupa en GameCube, no en «Sin consola»

        // Dos filas del mismo enlace y la misma fuente (como quedó NX2navi): la tarea termina y queda la más vieja.
        var id = PortId("prime-recomp");
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO port (console, game, project, kind, status, link, source, added) SELECT console, 'Driver', project, kind, status, link, source, added FROM port WHERE id = @id", new { id });
        await Done(Ports.Enqueue(f.Db));
        Assert.Equal(id, PortId("prime-recomp"));
    }

    /// <summary>#72: cada juego publica sus ports, la base compilada los trae y un cambio de ports cambia el hash de contenido.</summary>
    [Fact]
    public async Task PortsArePublishedWithTheirGameAndCompiled()
    {
        await Done(Ports.Enqueue(f.Db));
        var folder = f.Tmp["publicado"];
        Published.Write(f.Db, folder);
        var saga = File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "sagas"))[0]);
        Assert.Contains("\"ports\"", saga);
        Assert.Contains("\"project\": \"2ship2harkinian\"", saga);
        Assert.DoesNotContain("PsyDoom", string.Concat(Directory.GetFiles(Path.Combine(folder, "sin-saga")).Select(File.ReadAllText))); // sin juego no sale
        Assert.Contains("\"fullyPlayable\": true", saga); // 2ship2harkinian es 100 % jugable: Otteretro no interpreta el estado
        Assert.Contains("\"status\": \"no-port\"", string.Concat(Directory.GetFiles(Path.Combine(folder, "sin-saga")).Select(File.ReadAllText)));
        Assert.Empty(Published.Validate(folder));

        var first = Published.Compile(folder, f.Tmp["a.db.gz"]);
        var plain = f.Tmp["compilado.db"];
        using (var gz = new GZipStream(File.OpenRead(f.Tmp["a.db.gz"]), CompressionMode.Decompress))
        using (var file = File.Create(plain)) gz.CopyTo(file);
        using (var compiled = new SqliteConnection($"Data Source={plain};Pooling=false"))
        {
            compiled.Open();
            Assert.Equal(8, compiled.ExecuteScalar<long>("SELECT count(*) FROM port WHERE game_id IS NOT NULL"));
            Assert.Equal(1, compiled.ExecuteScalar<long>("SELECT count(*) FROM port WHERE status = 'no-port'"));
            Assert.Equal(3, compiled.ExecuteScalar<long>("SELECT emulator_level FROM port WHERE project = 'prime-recomp'"));
        }

        Assert.True(Ports.Assign(f.Db, PortId("PsyDoom"), 14));
        Published.Write(f.Db, folder);
        Assert.NotEqual(first.Content, Published.Compile(folder, f.Tmp["b.db.gz"]).Content);

        // Un port ya no listado se sigue publicando, con su marca.
        yaml.Remove("recomps/metroid-prime.yaml");
        await Done(Ports.Enqueue(f.Db));
        Published.Write(f.Db, folder);
        Assert.Contains("\"delisted\": true", string.Concat(Directory.GetFiles(Path.Combine(folder, "sin-saga")).Select(File.ReadAllText)));
        Assert.Empty(Published.Validate(folder));

        // Lo inválido no se publica: un 100 % jugable que no está completo, o un «sin port todavía» que no es una decompilación.
        var sagaFile = Directory.GetFiles(Path.Combine(folder, "sagas"))[0];
        File.WriteAllText(sagaFile, File.ReadAllText(sagaFile).Replace("\"fullyPlayable\": true", "\"fullyPlayable\": false"));
        Assert.Contains(Published.Validate(folder), e => e.Contains("100 % jugable"));
    }

    /// <summary>#71: el RSS entra por script; lo dudoso va a Claude (Sonnet), una sola vez por enlace, y nunca cambia lo que ya está.</summary>
    [Fact]
    public async Task NewPortsComeFromTheScriptAndClaudeOnlyVerifiesTheDoubtful()
    {
        await Done(Ports.Enqueue(f.Db));
        net.Rss = """
            <rss><channel>
              <item><title>Super Metroid (otro)</title><category>Recomp</category><pubDate>Wed, 04 Nov 2026 15:23:41 GMT</pubDate>
                <description>SNES · Fully playable · d/sm-recomp. Una recompilación.</description></item>
              <item><title>Metroid Prime</title><category>Recomp</category><description>GameCube · Experimental · a/prime-recomp. Ya está.</description></item>
              <item><title>Kart Native</title><category>Port</category><description>Native layer · Playable · e/kartnative. Algo.</description></item>
              <item><title>NxLibrary</title><category>Tool</category><description>Launcher · Experimental · f/nx. Una herramienta.</description></item>
            </channel></rss>
            """;
        net.Memo = """
            <h3 id="snes">SNES</h3><a href="https://github.com/N64Recomp/N64Recomp">Github</a><a href="https://github.com/g/smw">Super Mario World</a>
            <h2>Completed decomps without ports</h2><a href="https://github.com/h/nope">Sin port</a>
            """;
        ClaudeRunner.Launch = (start, stdin, ct) =>
        {
            calls.Add(stdin);
            Assert.Contains(PortSearch.Model, start.ArgumentList);
            var reply = new PortSearch.Reply([
                new("https://github.com/e/kartnative", false, "", "", "", "port", null, "experimental", "Es una capa para Mac de un juego de Switch, sin build."),
                new("https://github.com/g/smw", true, "snes", "Super Mario World", "SMW PC", "decomp", null, "playable", "El README dice que se juega con fallos."),
            ]);
            return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new
            {
                is_error = false,
                structured_output = reply,
                total_cost_usd = 0.01,
                usage = new { input_tokens = 100, output_tokens = 10, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            }, JsonSerializerOptions.Web), ""));
        };
        using (var c = f.Db.Open()) c.Execute("INSERT INTO game (id, name_es) VALUES (16, 'Super Mario World'); INSERT INTO version (id, game_id, platform, kind) VALUES (26, 16, 'snes', 'original')");

        await Done(PortSearch.Enqueue(f.Db));
        var input = Assert.Single(calls);
        Assert.Contains("e/kartnative", input);
        Assert.DoesNotContain("sm-recomp", input); // el script ya lo sabía
        Assert.DoesNotContain("h/nope", input);
        Assert.DoesNotContain("N64Recomp/N64Recomp", input);
        using (var c = f.Db.Open())
        {
            Assert.Equal(("snes", "complete", "2026-11-04"), c.QuerySingle<(string, string, string)>("SELECT console, status, added FROM port WHERE project = 'sm-recomp'"));
            Assert.Equal(16, c.ExecuteScalar<long>("SELECT game_id FROM port WHERE project = 'SMW PC'"));
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM port WHERE link LIKE '%kartnative%'"));
            Assert.Equal("playable", c.ExecuteScalar<string>("SELECT status FROM port WHERE project = 'prime-recomp'")); // el RSS no lo cambia
        }

        // Lo ya verificado (entrara o no) no se vuelve a pedir.
        await Done(PortSearch.Enqueue(f.Db));
        Assert.Single(calls);
    }

    [Fact]
    public void TitlesAreComparedLikeTheScript()
    {
        Assert.Equal("super mario odyssey", PortCompat.Norm("Super Mario Odyssey™"));
        Assert.Equal("pokemon snap", PortCompat.Norm("Pokémon Snap"));
        Assert.True(PortCompat.Same("Ninja Gaiden II", "Ninja Gaiden 2"));
        Assert.True(PortCompat.Same("The Legend of Zelda: Twilight Princess", "Legend of Zelda: Twilight Princess (GC)"));
        Assert.True(PortCompat.Same("Fable II (himdo)", "Fable II"));
        Assert.False(PortCompat.Same("Halo 3", "Halo: Combat Evolved"));
        Assert.False(PortCompat.Same("Crackdown", "Crackdown 2"));
        Assert.Equal(23, Ports.Extras("2026-01-01").Count);
    }
}

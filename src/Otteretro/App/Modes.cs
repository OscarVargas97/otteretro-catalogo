using System.Runtime.InteropServices;

namespace Otteretro.App;

/// <summary>Modo APP: servidor local + ventana.</summary>
public static class AppMode
{
    public static int Run(bool fullscreen)
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.NotifyExisting();
            return 0;
        }

        var configPath = AppConfig.DefaultPath;
        var config = AppConfig.Load(configPath);
        if (DataFolder.IsReady(config.DataFolder))
            Startup.PrepareData(config);

        var session = new AppSession(config, configPath, LocalServer.NewKey(), fullscreen);
        var app = LocalServer.Create(session);
        app.StartAsync().GetAwaiter().GetResult();
        var api = LocalServer.Address(app);
        _ = app.Services.GetRequiredService<Updater>().Check();

        // En desarrollo la interfaz viene de `next dev` y se le dice dónde está la API.
        var web = Environment.GetEnvironmentVariable("OTTERETRO_DEV_WEB");
        var url = web is null
            ? $"{api}/?key={session.Key}"
            : $"{web}/?key={session.Key}&api={Uri.EscapeDataString(api)}";

        MainWindow.Show(url, instance, fullscreen);
        app.StopAsync().GetAwaiter().GetResult();
        return 0;
    }
}

/// <summary>Modo CLI: `otteretro &lt;tarea&gt;`. Los comandos son la interfaz de la CLI y siguen en español.</summary>
public static class CliMode
{
    public const string Help = """
        Uso: otteretrocatalogo [comando]

          (sin comando)          Abre la app.
          --fullscreen           Abre la app a pantalla completa (en la sesión Play es automático).
          version                Muestra la versión.
          openapi <archivo>      Escribe el contrato OpenAPI de la API (para generar los tipos del front).
          catalogo cargar        Carga el catálogo desde IGDB: sin opciones, las 24 consolas y los juegos
                                 de PC populares; o solo
            [--consola X]          una consola (snes, n64, switch, ps2…),
            [--saga X]             una saga (zelda, half-life…; PC se carga así)
            [--pc-populares]       o los 1.500 juegos de PC más calificados. Lo hace la app abierta.
          catalogo importar <base>     Trae una copia de un catalogo.db a este catálogo vacío.
          catalogo base <sha>          De qué commit de main parte el catálogo local: publicar avisa si alguien cambió lo mismo después.
          catalogo publicar <carpeta>  Escribe el catálogo como un JSON por saga.
          catalogo validar <carpeta>   Revisa lo publicado (corre en cada PR).
          catalogo compilar <carpeta> <archivo>  Genera la base que baja la app y su manifiesto.
          catalogo depurar       Claude revisa las sagas dudosas del catálogo (repetidos, juegos que no son de la saga,
                                 sueltos que sí lo son) de noche y aplica los cambios solo; lo hace la app abierta.
            [--contar]             Solo cuenta los casos y estima el gasto, sin llamar a Claude.
          catalogo deshacer <n>  Deshace el cambio n de Claude (el número sale en el registro).
          claude ahora           Lanza ya la carga de Claude, fuera de su horario (00:00 a 08:00), hasta que empiece el siguiente.
          claude cargar          Carga inicial con Claude, por partes (Nintendo, Sony, Microsoft y PC): cada parte se depura
                                 y después Claude prepara rutas, icónico y mejor versión para que las apruebes. Dura días.
            [--parte X]            Solo una parte (nintendo, sony, microsoft o pc).
          tareas prueba          Encola una tarea de prueba (10 pasos de 3 s) para ver el panel de tareas.
          servidor               Levanta la API y la interfaz sin ventana, para abrirlas en un navegador (desarrollo).
          ayuda                  Muestra esta ayuda.
        """;

    public static int Run(string[] args, TextWriter? output = null)
    {
        if (output is null)
        {
            AttachToConsole();
            output = Console.Out;
        }
        switch (args)
        {
            case ["version"]:
                output.WriteLine(Startup.Version);
                return 0;
            case ["ayuda"] or ["--help"] or ["-h"]:
                output.WriteLine(Help);
                return 0;
            case ["openapi", var file]:
                WriteOpenApi(file).GetAwaiter().GetResult();
                output.WriteLine($"Contrato escrito en {file}");
                return 0;
            case ["tareas", "prueba"]:
                return Enqueue(output, db => [TaskQueue.Enqueue(db, "demo")]);
            case ["claude", "ahora"]:
                return WithData(output, db =>
                    output.WriteLine($"Claude corre ya (tarea {Claude.ClaudeBudget.RunNow(db)}) hasta que empiece su horario; el límite de la suscripción y el tope, si está marcado, siguen valiendo."));
            case ["claude", "cargar", .. var load] when load is [] or ["--parte", _]:
                return WithData(output, db =>
                {
                    var part = load is [_, var p] ? p : null;
                    output.WriteLine(Claude.InitialLoad.Enqueue(db, part) is { } id
                        ? $"Tarea {id} encolada; la app abierta la toma y Claude trabaja en su horario."
                        : $"Parte desconocida: {part}. Partes: {string.Join(", ", Claude.InitialLoad.Parts.Select(x => x.Id))}.");
                });
            case ["catalogo", "base", var sha]:
                return WithData(output, db =>
                {
                    Catalog.GitHubPublisher.SetBase(db, sha);
                    output.WriteLine($"El catálogo parte de {sha}.");
                });
            case ["catalogo", "importar", var from]:
                return WithData(output, db => output.WriteLine(Catalog.InitialCatalog.Import(db, Path.GetFullPath(from))
                    ? "Catálogo importado." : "Este catálogo ya tiene juegos: no se tocó."));
            case ["catalogo", "publicar", var folder]:
                return WithData(output, db =>
                {
                    Catalog.Published.Write(db, Path.GetFullPath(folder));
                    output.WriteLine($"Publicado en {folder}");
                });
            case ["catalogo", "validar", var folder]:
            {
                var errors = Catalog.Published.Validate(Path.GetFullPath(folder));
                foreach (var error in errors) output.WriteLine(error);
                output.WriteLine(errors.Count == 0 ? "Sin errores." : $"{errors.Count} errores.");
                return errors.Count == 0 ? 0 : 1;
            }
            case ["catalogo", "compilar", var folder, var file]:
            {
                var manifest = Catalog.Published.Compile(Path.GetFullPath(folder), Path.GetFullPath(file));
                File.WriteAllText(Path.GetFullPath(file) + ".json", System.Text.Json.JsonSerializer.Serialize(manifest, System.Text.Json.JsonSerializerOptions.Web));
                output.WriteLine($"{manifest.Sagas} sagas, {manifest.Games} juegos, {manifest.Versions} versiones. SHA-256: {manifest.Sha256}");
                return 0;
            }
            case ["catalogo", "depurar", "--contar"]:
                return WithData(output, db =>
                {
                    var n = Claude.CatalogCleanup.Measure(db);
                    output.WriteLine($"{n.Sagas} sagas dudosas con {n.Games} juegos, y {n.Loose} juegos sueltos que parecen de alguna.");
                    foreach (var (reason, count) in n.Reasons) output.WriteLine($"  {reason}: {count} sagas");
                    output.WriteLine($"Unas {n.Calls} llamadas a Claude (Haiku), unos {n.Tokens:N0} tokens. Lo que no cambie no se vuelve a pedir.");
                });
            case ["catalogo", "depurar"]:
                return Enqueue(output, db => [Claude.CatalogCleanup.Enqueue(db)]);
            case ["catalogo", "deshacer", var learning] when long.TryParse(learning, out var id):
                return WithData(output, db => output.WriteLine(Claude.CatalogCleanup.Undo(db, id)
                    ? $"Cambio {id} deshecho." : $"No hay un cambio {id} sin deshacer."));
            case ["catalogo", "cargar", .. var options] when options is [] or ["--consola", _] or ["--saga", _, ..] or ["--pc-populares"]:
                return Enqueue(output, db => EnqueueLoad(db, options));
            case ["servidor"]:
                Serve(output);
                return 0;
            default:
                output.WriteLine($"Comando desconocido: {string.Join(' ', args)}");
                output.WriteLine(Help);
                return 1;
        }
    }

    /// <summary>
    /// `catalogo cargar`: sin opciones, una tarea por consola (PC y PC VR no: tiene demasiados juegos y va por saga)
    /// y, al final, los juegos de PC populares; `--consola X`, `--saga X` (en varias palabras sin comillas) o `--pc-populares`.
    /// </summary>
    public static long[] EnqueueLoad(Data.Database db, string[] options)
    {
        if (options is ["--consola", var platform]) return [Catalog.IgdbImport.EnqueuePlatform(db, platform.ToLowerInvariant())];
        if (options is ["--saga", .. var words]) return [Catalog.IgdbImport.EnqueueSaga(db, string.Join(' ', words))];
        if (options is ["--pc-populares"]) return [Catalog.IgdbImport.EnqueuePopular(db)];
        using var c = db.Open();
        var consoles = Dapper.SqlMapper.Query<string>(c, "SELECT id FROM console WHERE id NOT IN ('pc', 'pcvr') ORDER BY rowid").ToList();
        return [.. consoles.Select(p => Catalog.IgdbImport.EnqueuePlatform(db, p)), Catalog.IgdbImport.EnqueuePopular(db)];
    }

    /// <summary>Encola tareas en la base de la carpeta de datos; las toma la app abierta.</summary>
    static int Enqueue(TextWriter output, Func<Data.Database, long[]> enqueue) => WithData(output, db =>
    {
        var ids = enqueue(db);
        output.WriteLine(ids.Length == 1
            ? $"Tarea {ids[0]} encolada; la app abierta la toma en unos segundos."
            : $"{ids.Length} tareas encoladas; la app abierta las hace de a una y se ven en el panel de tareas.");
    });

    static int WithData(TextWriter output, Action<Data.Database> action)
    {
        var config = AppConfig.Load(AppConfig.DefaultPath);
        if (!DataFolder.IsReady(config.DataFolder))
        {
            output.WriteLine("Todavía no hay carpeta de datos: abre la app y elige una.");
            return 1;
        }
        action(Startup.PrepareData(config));
        return 0;
    }

    static void Serve(TextWriter output)
    {
        var configPath = AppConfig.DefaultPath;
        var config = AppConfig.Load(configPath);
        if (DataFolder.IsReady(config.DataFolder))
            Startup.PrepareData(config);
        var session = new AppSession(config, configPath, LocalServer.NewKey());
        var app = LocalServer.Create(session);
        app.StartAsync().GetAwaiter().GetResult();
        output.WriteLine($"Configuración: {configPath}");
        output.WriteLine($"Abre {LocalServer.Address(app)}/?key={session.Key}");
        output.WriteLine("Ctrl+C para terminar.");
        app.WaitForShutdownAsync().GetAwaiter().GetResult();
    }

    static async Task WriteOpenApi(string file)
    {
        var session = new AppSession(new AppConfig(), Path.GetTempFileName(), LocalServer.NewKey());
        await using var app = LocalServer.Create(session);
        await app.StartAsync();
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"{LocalServer.Address(app)}/openapi/v1.json");
        await File.WriteAllTextAsync(file, json);
        await app.StopAsync();
    }

    // El ejecutable es WinExe (sin consola); al usarlo desde una terminal se engancha a ella.
    static void AttachToConsole()
    {
        if (OperatingSystem.IsWindows()) AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}

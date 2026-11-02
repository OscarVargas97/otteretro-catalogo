using System.Text.Json.Serialization;
using Otteretro.Api;
using Velopack;
using Velopack.Sources;

namespace Otteretro.App;

[JsonConverter(typeof(JsonStringEnumConverter<UpdatePhase>))]
public enum UpdatePhase { None, Unavailable, Checking, UpToDate, Downloading, Ready, Error }

/// <param name="CheckedAt">Cuándo terminó la última comprobación (para mostrar "comprobado a las …").</param>
/// <param name="Message">Código del motivo (clave `update.*` del catálogo del front, #142).</param>
public record UpdateState(UpdatePhase Phase, string? Version, int Progress, string? Message, DateTimeOffset? CheckedAt);

/// <summary>
/// Busca una versión nueva, la descarga y avisa a la interfaz en cada paso (panel de tareas y aviso de "lista").
/// Se instala al cerrar la app o al pulsar "Reiniciar ahora".
/// El origen sale del canal (#180): las releases de GitHub (estables o de prueba) u otro origen que elige la persona.
/// Velopack solo actualiza en Windows: en Linux, el programa se actualiza con el sistema (Nix, #114). Es el mismo sistema de Otteretro (#12).
/// </summary>
public sealed class Updater(AppSession session, Events events)
{
    UpdateManager? manager;
    VelopackAsset? ready;
    int busy;

    public UpdateState State { get; private set; } = OperatingSystem.IsWindows()
        ? new(UpdatePhase.None, null, 0, null, null)
        : new(UpdatePhase.Unavailable, null, 0, "withSystem", null);
    DateTimeOffset? checkedAt;

    /// <summary>
    /// Se llama al abrir la app y desde el botón "Buscar actualización". Termina cuando acaba la comprobación;
    /// si hay versión nueva, la descarga sigue en segundo plano. No hace nada si ya está buscando o hay una lista.
    /// </summary>
    public async Task Check()
    {
        // Una sola búsqueda o descarga a la vez (el arranque y el botón pueden coincidir).
        if (!OperatingSystem.IsWindows() || State.Phase == UpdatePhase.Ready || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        var downloading = false;
        try
        {
            if (SourceFor(session.Config.EffectiveUpdateChannel, Origin(session)) is not { } found)
            {
                Set(UpdatePhase.Unavailable, null, 0, "noSource");
                return;
            }
            // Sin VelopackApp (las pruebas, `servidor` desde el código) no hay instalación que actualizar.
            try { manager = new UpdateManager(found.Source, new UpdateOptions { ExplicitChannel = found.Channel }); }
            catch (InvalidOperationException) { manager = null; }
            if (manager is not { IsInstalled: true })
            {
                Set(UpdatePhase.Unavailable, null, 0, "notInstalled");
                return;
            }
            Set(UpdatePhase.Checking, null, 0);
            // Un origen en una unidad de red caída no debe dejar el botón colgado en "Buscando".
            var info = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromMinutes(1));
            checkedAt = DateTimeOffset.Now;
            if (info is null)
            {
                Set(UpdatePhase.UpToDate, null, 0);
                return;
            }
            Set(UpdatePhase.Downloading, info.TargetFullRelease.Version.ToString(), 0);
            downloading = true;
            _ = Download(manager, info);
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            if (!downloading) Volatile.Write(ref busy, 0);
        }
    }

    async Task Download(UpdateManager manager, UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version.ToString();
        try
        {
            await manager.DownloadUpdatesAsync(info, p => Set(UpdatePhase.Downloading, version, p));
            ready = info.TargetFullRelease;
            // Si no se reinicia antes, se instala al cerrar la app.
            manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
            Set(UpdatePhase.Ready, version, 100);
            FileLog.Write(LogLevel.Information, "Updater", $"Versión {version} descargada; se instala al cerrar la app.");
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }

    public const string Repo = "https://github.com/OscarVargas97/otteretro-catalogo";

    /// <summary>
    /// Donde `scripts/empaquetar.ps1` deja los paquetes en cada disco de Otteretro: la misma carpeta `Paquetes` que usa Otteretro
    /// (decisión 244 de Otteretro), en una subcarpeta propia, porque el índice de Velopack se llama igual para los dos programas.
    /// </summary>
    public static readonly string PackagesFolder = Path.Combine("Otteretro", "Paquetes", "OtteretroCatalogo");

    /// <summary>La carpeta de paquetes más reciente de los discos de Otteretro conectados; null si ninguno tiene.</summary>
    public static string? NewestPackages(IEnumerable<string>? roots = null) =>
        (roots ?? DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name))
            .Where(r => File.Exists(Path.Combine(r, "Otteretro", "otteretro-ubicacion.json")))
            .Select(r => Path.Combine(r, PackagesFolder))
            .Where(f => File.Exists(Path.Combine(f, "releases.win.json")))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(f, "releases.win.json"))).FirstOrDefault();

    /// <summary>La carpeta del canal Local sale de tus discos, como en Otteretro; la de Otro origen, de lo que escribiste.</summary>
    static string? Origin(AppSession session) => session.Config.EffectiveUpdateChannel == "local" ? NewestPackages() : session.Config.UpdateSource;

    /// <summary>
    /// El origen de cada canal (#180) y el canal de Velopack que busca: «win» las releases de main, «dev» las prereleases
    /// de los PR abiertos (#12). Otro origen: el repo de GitHub, una URL o una carpeta de quien tiene su propia versión. Null si no hay.
    /// </summary>
    public static (IUpdateSource Source, string Channel)? SourceFor(string channel, string? origin) => channel switch
    {
        "stable" => (new GithubSource(Repo, null, false), "win"),
        "testing" => (new GithubSource(Repo, null, true), "dev"),
        _ when string.IsNullOrWhiteSpace(origin) => null,
        "local" => (new SimpleFileSource(new DirectoryInfo(origin)), "win"),
        _ when origin.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) => (new GithubSource(origin, null, false), "win"),
        _ when Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" => (new SimpleWebSource(uri), "win"),
        _ => (new SimpleFileSource(new DirectoryInfo(origin)), "win"),
    };

    /// <summary>Cambia de canal: lo de antes ya no vale (una versión descargada de otro canal tampoco).</summary>
    public void Forget()
    {
        if (State.Phase is UpdatePhase.Checking or UpdatePhase.Downloading) return;
        manager = null;
        ready = null;
        Set(OperatingSystem.IsWindows() ? UpdatePhase.None : UpdatePhase.Unavailable, null, 0, OperatingSystem.IsWindows() ? null : "withSystem");
    }

    void Fail(Exception e)
    {
        Set(UpdatePhase.Error, State.Version, 0, "failed");
        FileLog.Write(LogLevel.Error, "Updater", e.ToString());
    }

    /// <summary>Instala la versión descargada y vuelve a abrir la app. Devuelve false si no hay ninguna lista.</summary>
    public bool RestartNow()
    {
        if (manager is null || ready is null) return false;
        manager.ApplyUpdatesAndRestart(ready);
        return true;
    }

    void Set(UpdatePhase phase, string? version, int progress, string? message = null)
    {
        State = new(phase, version, progress, message, checkedAt);
        events.Publish("update", State);
    }
}

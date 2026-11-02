using Microsoft.AspNetCore.Http.HttpResults;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Api;

public record AppState(string Version, string? DataFolder, bool Configured, string Language, bool Fullscreen, bool MenuCollapsed);
public record FolderRequest(string Path);
public record LanguageRequest(string Language);
public record MenuRequest(bool Collapsed);
public record OpenLinkRequest(string Url);
/// <param name="Channel">stable, testing o custom (#180).</param>
/// <param name="Origin">Con custom: carpeta, URL o repo de GitHub.</param>
/// <param name="Origin">Con «custom», el otro origen; con «local», la carpeta de los paquetes de este equipo (#163).</param>
/// <param name="Local">La carpeta local guardada, para volver a ella de un clic.</param>
public record CredentialRequest(string Value);

/// <summary>
/// Error o aviso para la persona, como código y parámetros: el front lo traduce con su catálogo (#142).
/// Los códigos son las claves de `errors.*` en web/messages.
/// </summary>
public record ApiError(string Code, Dictionary<string, string>? Params = null)
{
    public static ApiError Of(string code, params (string Key, string Value)[] values) =>
        new(code, values.Length == 0 ? null : values.ToDictionary(v => v.Key, v => v.Value));
}
/// <summary>Un disco para la carpeta de datos: la carpeta que propone y si es la sugerida.</summary>
public record Drive(string Name, long Free, long Total, string Folder, bool Suggested);
/// <param name="Used">Null mientras se mide por primera vez.</param>
public record FolderInfo(string? Path, long? Used, long FreeOnDrive);

/// <summary>Ajustes › General › Imágenes (#9): el disco ★ donde están y, sin Otteretro, los discos donde se puede poner.</summary>
public record ImagesFolderInfo(Catalog.ImageHomeInfo Home, List<Catalog.StarDisk> Disks);

/// <param name="Star">La carpeta del ★ (`&lt;disco&gt;\Otteretro`).</param>
public record StarRequest(string? Star);

/// <param name="Origin">Con custom: carpeta, URL o repo de GitHub.</param>
/// <param name="Local">La carpeta que encontró en tus discos el canal Local; null si ninguno tiene paquetes.</param>
public record UpdateChannelInfo(string Channel, string? Origin, string? Local = null);

/// <summary>Endpoints base de F0. Cada módulo añadirá los suyos.</summary>
public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/state", (AppSession s) => State(s));

        api.MapPost("/data-folder", Results<Ok<AppState>, BadRequest<ApiError>, Conflict<ApiError>> (FolderRequest request, AppSession s, FolderMoveService m, TaskQueue q) =>
        {
            if (m.State.Running) return TypedResults.Conflict(ApiError.Of("move.inProgress"));
            if (q.Busy) return TypedResults.Conflict(ApiError.Of("move.tasksRunning"));
            var error = DataFolder.Prepare(request.Path);
            if (error is not null) return TypedResults.BadRequest(error);
            s.Config.DataFolder = request.Path;
            s.Config.Save(s.ConfigPath);
            Startup.PrepareData(s.Config);
            return TypedResults.Ok(State(s));
        });

        api.MapGet("/data-folder", (AppSession s) =>
        {
            var folder = s.Config.DataFolder;
            var root = folder is null ? null : Mounts.Of(folder);
            return new FolderInfo(folder, folder is null ? 0 : FolderMove.RememberedSize(folder),
                root is not null && Directory.Exists(root) ? new DriveInfo(root).AvailableFreeSpace : 0);
        });

        // «Abrir carpeta» (#111): la abre en el explorador de archivos del sistema.
        api.MapPost("/data-folder/open", Results<Ok, Conflict<ApiError>> (AppSession s) =>
            s.Config.DataFolder is { } folder && Directory.Exists(folder)
                ? Open(folder) ? TypedResults.Ok() : TypedResults.Conflict(ApiError.Of("link.openFailed"))
                : TypedResults.Conflict(ApiError.Of("move.noDataFolder")));

        api.MapGet("/data-folder/move", (FolderMoveService m) => m.State);

        api.MapPost("/data-folder/move", Results<Accepted<MoveState>, BadRequest<ApiError>> (FolderRequest request, FolderMoveService m, TaskQueue q) =>
        {
            // Las tareas escriben en la base: se espera a que terminen antes de mudarla.
            var error = q.Busy ? ApiError.Of("move.tasksRunning") : m.Start(request.Path);
            return error is null ? TypedResults.Accepted("/api/data-folder/move", m.State) : TypedResults.BadRequest(error);
        });

        api.MapPut("/language", Results<Ok<AppState>, BadRequest<ApiError>> (LanguageRequest request, AppSession s) =>
        {
            if (!AppConfig.Languages.Contains(request.Language)) return TypedResults.BadRequest(ApiError.Of("language.unsupported", ("language", request.Language)));
            s.Config.Language = request.Language;
            s.Config.Save(s.ConfigPath);
            return TypedResults.Ok(State(s));
        });

        // Retraer o expandir el menú lateral (#130): se recuerda en config.json.
        api.MapPut("/menu", (MenuRequest request, AppSession s) =>
        {
            s.Config.MenuCollapsed = request.Collapsed;
            s.Config.Save(s.ConfigPath);
            return State(s);
        });

        api.MapGet("/drives", () => Mounts.Offer(DriveInfo.GetDrives()
                .Where(d => d.IsReady && (!OperatingSystem.IsWindows() || d.DriveType == DriveType.Fixed))
                .Select(d => (d.Name, d.DriveFormat, d.TotalFreeSpace, d.TotalSize)),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows()));

        api.MapGet("/images-folder", Results<Ok<ImagesFolderInfo>, Conflict<ApiError>> (AppSession s) =>
            DataFolder.IsReady(s.Config.DataFolder) ? TypedResults.Ok(ImagesFolder(DataFolder.Open(s.Config.DataFolder!))) : TypedResults.Conflict(ApiError.Of("move.noDataFolder")));

        // Sin Otteretro: preparar el ★ como lo haría Otteretro, o cambiarlo (lleva las imágenes).
        api.MapPut("/images-folder", Results<Ok<ImagesFolderInfo>, BadRequest<ApiError>, Conflict<ApiError>> (StarRequest request, AppSession s) =>
        {
            if (!DataFolder.IsReady(s.Config.DataFolder)) return TypedResults.Conflict(ApiError.Of("move.noDataFolder"));
            var db = DataFolder.Open(s.Config.DataFolder!);
            return Catalog.ImageHome.SetStar(db, request.Star) is { } error ? TypedResults.BadRequest(error) : TypedResults.Ok(ImagesFolder(db));
        });

        api.MapGet("/update", (Updater u) => u.State);

        // Responde cuando termina la comprobación; si hay descarga, su progreso llega por el evento "update".
        api.MapPost("/update/check", async (Updater u) =>
        {
            await u.Check();
            return u.State;
        });

        // Ajustes › General › Actualizaciones (#12): el canal y, con «custom», el origen. Al cambiarlo se busca de inmediato.
        static UpdateChannelInfo ChannelOf(AppConfig c) => new(c.EffectiveUpdateChannel, c.UpdateSource, Updater.NewestPackages());
        api.MapGet("/update/channel", (AppSession s) => ChannelOf(s.Config));
        api.MapPut("/update/channel", async Task<Results<Ok<UpdateChannelInfo>, BadRequest<ApiError>>> (UpdateChannelInfo request, AppSession s, Updater u) =>
        {
            if (!AppConfig.UpdateChannels.Contains(request.Channel)) return TypedResults.BadRequest(ApiError.Of("update.channel"));
            if (request.Channel == "custom" && string.IsNullOrWhiteSpace(request.Origin)) return TypedResults.BadRequest(ApiError.Of("update.noOrigin"));
            s.Config.UpdateChannel = request.Channel;
            if (request.Channel == "custom") s.Config.UpdateSource = request.Origin!.Trim();
            s.Config.Save(s.ConfigPath);
            u.Forget();
            _ = u.Check();
            return TypedResults.Ok(ChannelOf(s.Config));
        });

        api.MapPost("/update/restart", Results<Ok, Conflict<ApiError>> (Updater u) =>
            u.RestartNow() ? TypedResults.Ok() : TypedResults.Conflict(ApiError.Of("update.nothingDownloaded")));

        // Ajustes › Cuentas y claves (§5.6): nunca se devuelve un valor, solo si está guardado.
        api.MapGet("/credentials", Results<Ok<List<CredentialStatus>>, Conflict<ApiError>> (AppSession s, SecretBox box) =>
            CredentialsOf(s, box) is { } c ? TypedResults.Ok(c.List()) : TypedResults.Conflict(ApiError.Of("move.noDataFolder")));

        api.MapPut("/credentials/{id}", Results<Ok<List<CredentialStatus>>, BadRequest<ApiError>, Conflict<ApiError>, NotFound> (string id, CredentialRequest request, AppSession s, SecretBox box) =>
        {
            if (!Credentials.IsKnown(id)) return TypedResults.NotFound();
            if (string.IsNullOrWhiteSpace(request.Value)) return TypedResults.BadRequest(ApiError.Of("credentials.empty"));
            if (CredentialsOf(s, box) is not { } c) return TypedResults.Conflict(ApiError.Of("move.noDataFolder"));
            try { c.Save(id, request.Value.Trim()); }
            catch (KeyringUnavailableException e)
            {
                FileLog.Write(LogLevel.Warning, "Credentials", $"Sin llavero: {e.Message}");
                return TypedResults.Conflict(ApiError.Of("credentials.noKeyring"));
            }
            return TypedResults.Ok(c.List());
        });

        api.MapDelete("/credentials/{id}", Results<Ok<List<CredentialStatus>>, Conflict<ApiError>, NotFound> (string id, AppSession s, SecretBox box) =>
        {
            if (!Credentials.IsKnown(id)) return TypedResults.NotFound();
            if (CredentialsOf(s, box) is not { } c) return TypedResults.Conflict(ApiError.Of("move.noDataFolder"));
            c.Delete(id);
            return TypedResults.Ok(c.List());
        });

        api.MapPost("/credentials/{id}/open", Results<Ok, NotFound, Conflict<ApiError>> (string id) =>
        {
            if (Credentials.Kinds.FirstOrDefault(k => k.Id == id) is not { } kind) return TypedResults.NotFound();
            return Open(kind.Url) ? TypedResults.Ok() : TypedResults.Conflict(ApiError.Of("link.openFailed"));
        });

        CatalogEndpoints.Map(api);
        ClaudeEndpoints.Map(api);

        // Panel de tareas: lo pendiente y lo reciente; cada cambio llega después por el evento "task".
        api.MapGet("/tasks", (TaskQueue q) => q.List());
        // Registro de una tarea (F2.10) y «Limpiar terminadas» (F2.11).
        api.MapGet("/tasks/{id:long}/log", Results<Ok<List<TaskLogEntry>>, Conflict<ApiError>> (long id, AppSession s) =>
            CatalogEndpoints.Db(s) is { } db ? TypedResults.Ok(TaskQueue.ReadLog(db, id)) : CatalogEndpoints.NoData());
        api.MapDelete("/tasks/finished", Results<Ok<int>, Conflict<ApiError>> (AppSession s) =>
            CatalogEndpoints.Db(s) is { } db ? TypedResults.Ok(TaskQueue.Clear(db)) : CatalogEndpoints.NoData());
        // Pausar, reanudar y cancelar desde el panel (#146).
        api.MapPost("/tasks/{id:long}/pause", Results<Ok, NotFound, Conflict<ApiError>> (long id, AppSession s, TaskQueue queue) =>
            CatalogEndpoints.Db(s) is not { } db ? CatalogEndpoints.NoData() : queue.Pause(db, id) ? TypedResults.Ok() : TypedResults.NotFound());
        api.MapPost("/tasks/{id:long}/resume", Results<Ok, NotFound, Conflict<ApiError>> (long id, AppSession s, TaskQueue queue) =>
            CatalogEndpoints.Db(s) is not { } db ? CatalogEndpoints.NoData() : queue.Resume(db, id) ? TypedResults.Ok() : TypedResults.NotFound());
        api.MapPost("/tasks/{id:long}/cancel", Results<Ok, NotFound, Conflict<ApiError>> (long id, AppSession s, TaskQueue queue) =>
            CatalogEndpoints.Db(s) is not { } db ? CatalogEndpoints.NoData() : queue.Cancel(db, id) ? TypedResults.Ok() : TypedResults.NotFound());
        // Quitar una terminada desde su ✕ en el panel (#172); una que corre o espera no se toca.
        api.MapDelete("/tasks/{id:long}", Results<Ok<int>, Conflict<ApiError>> (long id, AppSession s) =>
            CatalogEndpoints.Db(s) is { } db ? TypedResults.Ok(TaskQueue.Clear(db, id)) : CatalogEndpoints.NoData());

        // Portada, héroe o logo de un juego desde catalogo/imagenes. Va en <img src>, así que la clave llega en ?key=.
        api.MapGet("/images/{gameId:long}/{kind}", Results<PhysicalFileHttpResult, NotFound> (long gameId, string kind, AppSession s, HttpResponse response) =>
        {
            // La ventana pregunta siempre (con ETag): si la imagen cambió, no se queda con la vieja (#58).
            response.Headers.CacheControl = "no-cache";
            if (!Catalog.SteamGridDb.Kinds.Contains(kind) || !DataFolder.IsReady(s.Config.DataFolder)) return TypedResults.NotFound();
            var file = Catalog.SteamGridDb.Find(DataFolder.Open(s.Config.DataFolder!), gameId, kind);
            return file is null ? TypedResults.NotFound() : TypedResults.PhysicalFile(file, ImageType(file), enableRangeProcessing: false);
        });

        api.MapGet("/events", (HttpContext http, Events events) => events.Serve(http)).ExcludeFromDescription();
    }

    /// <summary>Abre un enlace con el programa del sistema; false si no hay quién lo abra.</summary>
    internal static bool Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            FileLog.Write(LogLevel.Warning, "Open", $"No se pudo abrir {url}: {e.Message}");
            return false;
        }
    }

    static ImagesFolderInfo ImagesFolder(Data.Database db) => new(Catalog.ImageHome.Info(db), Catalog.ImageHome.Disks(DriveInfo.GetDrives()
        .Where(d => d.IsReady && (!OperatingSystem.IsWindows() || d.DriveType == DriveType.Fixed)).Select(d => (d.Name, d.AvailableFreeSpace))));

    static string ImageType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    static Credentials? CredentialsOf(AppSession s, SecretBox box) =>
        DataFolder.IsReady(s.Config.DataFolder) ? new(DataFolder.Open(s.Config.DataFolder!), s.Config.MachineId, box) : null;

    static AppState State(AppSession s) =>
        new(Startup.Version, s.Config.DataFolder, DataFolder.IsReady(s.Config.DataFolder), s.Config.EffectiveLanguage, s.Fullscreen, s.Config.MenuCollapsed);
}

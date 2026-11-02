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

/// <summary>Ajustes › General › Imágenes (#9).</summary>
/// <param name="Shared">Usa una carpeta común y no la propia.</param>
/// <param name="Available">La carpeta en uso existe; con un disco desconectado, no.</param>
public record ImagesFolderInfo(string Path, bool Shared, bool Available, List<Catalog.SteamGridDb.OtteretroDisk> Disks);

/// <param name="Path">La carpeta común; null vuelve a la propia.</param>
public record ImagesFolderRequest(string? Path);

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

        api.MapPut("/images-folder", Results<Ok<ImagesFolderInfo>, BadRequest<ApiError>, Conflict<ApiError>> (ImagesFolderRequest request, AppSession s) =>
        {
            if (!DataFolder.IsReady(s.Config.DataFolder)) return TypedResults.Conflict(ApiError.Of("move.noDataFolder"));
            var path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path.Trim();
            if (path is not null && (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)))
                return TypedResults.BadRequest(ApiError.Of("images.folderMissing", ("path", path)));
            var db = DataFolder.Open(s.Config.DataFolder!);
            Catalog.SteamGridDb.SetSharedFolder(db, path);
            return TypedResults.Ok(ImagesFolder(db));
        });

        // Ajustes › General › Actualizaciones (#180): el canal y, con «custom», el origen. Al cambiarlo se busca de inmediato.

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

    static ImagesFolderInfo ImagesFolder(Data.Database db)
    {
        var folder = Catalog.SteamGridDb.Folder(db);
        var roots = DriveInfo.GetDrives().Where(d => d.IsReady && (!OperatingSystem.IsWindows() || d.DriveType == DriveType.Fixed)).Select(d => d.Name);
        // En Linux, Otteretro suele quedar en la carpeta personal (#114), que no es un montaje.
        if (!OperatingSystem.IsWindows()) roots = roots.Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return new(folder, Catalog.SteamGridDb.SharedFolder(db) is not null, Directory.Exists(folder), Catalog.SteamGridDb.OtteretroDisks(roots.Distinct()));
    }

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

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Otteretro.Api;

namespace Otteretro.App;

/// <param name="Step">Código del paso: preparing, moving, copying, verifying, deletingSource, done o error (clave `move.step.*` del front, #142).</param>
public record MoveState(string Step, long Done, long Total, bool Running, string? Destination, ApiError? Error, ApiError? Warning);

/// <summary>Ejecuta la mudanza en segundo plano y la publica como evento "move" (panel de tareas y Ajustes).</summary>
public sealed class FolderMoveService(AppSession session, Events events)
{
    public MoveState State { get; private set; } = new("", 0, 0, false, null, null, null);
    readonly Lock gate = new();

    /// <summary>Devuelve un error si no puede empezar; si empieza, el resultado llega por eventos.</summary>
    public ApiError? Start(string destination)
    {
        string source;
        // Comprobar y marcar "en curso" de una vez: dos mudanzas a la vez pueden borrar origen y destino.
        lock (gate)
        {
            if (State.Running) return ApiError.Of("move.inProgress");
            if (session.Config.DataFolder is not { } current) return ApiError.Of("move.noDataFolder");
            source = current;
            var error = FolderMove.Validate(source, destination);
            if (error is not null) return error;
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            Set(new("preparing", 0, 0, true, destination, null, null));
        }
        // ponytail: en F0 nada más escribe en la carpeta de datos; cuando haya tareas que la usen, pausarlas antes de mudarse.
        _ = Task.Run(() =>
        {
            FileLog.Folder = Path.Combine(Path.GetDirectoryName(AppConfig.DefaultPath)!, "logs");
            try
            {
                FolderMove.CopyAndVerify(source, destination, p => Set(State with { Step = p.Step, Done = p.Done, Total = p.Total }));
                try
                {
                    session.Config.DataFolder = destination;
                    session.Config.Save(session.ConfigPath);
                }
                catch
                {
                    // config.json sigue apuntando al origen (se escribe de forma atómica): se deshace la copia o el movimiento.
                    session.Config.DataFolder = source;
                    FolderMove.Undo(source, destination);
                    throw;
                }
                Set(State with { Step = "deletingSource" });
                var warning = FolderMove.DeleteSource(source);
                FileLog.Folder = DataFolder.LogsPath(destination);
                FileLog.Write(LogLevel.Information, "FolderMove", $"Carpeta de datos movida de {source} a {destination}.");
                Set(State with { Step = "done", Running = false, Warning = warning });
            }
            catch (Exception e)
            {
                // Si algo falló al deshacer, se dice dónde quedaron de verdad los datos.
                var where = DataFolder.IsReady(source) ? source : destination;
                FileLog.Folder = DataFolder.LogsPath(where);
                FileLog.Write(LogLevel.Error, "FolderMove", e.ToString());
                var mismatch = e as FolderMove.CopyMismatchException;
                var detail = mismatch?.RelativePath ?? e.Message;
                var error = where == source
                    ? ApiError.Of(mismatch is null ? "move.failedNothingMoved" : "move.copyMismatch", ("path", source), ("detail", detail))
                    : ApiError.Of("move.failedDataAt", ("path", destination), ("detail", detail));
                Set(State with { Step = "error", Running = false, Error = error });
            }
        });
        return null;
    }

    void Set(MoveState state)
    {
        State = state;
        events.Publish("move", state);
    }
}

/// <summary>
/// Mueve la carpeta de datos a otra ubicación sin perder nada (P7):
/// copiar → verificar cada archivo por hash → (quien llama cambia la configuración) → borrar el origen.
/// En el mismo disco se mueve sin copiar.
/// </summary>
public static class FolderMove
{
    /// <summary>Un archivo copiado no coincide con el original al compararlos por hash.</summary>
    public sealed class CopyMismatchException(string relativePath) : IOException(relativePath)
    {
        public string RelativePath { get; } = relativePath;
    }

    public record MoveProgress(string Step, long Done, long Total);

    static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    /// <summary>Devuelve el error o null si se puede mover de <paramref name="source"/> a <paramref name="destination"/>.</summary>
    public static ApiError? Validate(string source, string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            return ApiError.Of("path.notAbsolute");
        var s = Normalize(source);
        var d = Normalize(destination);
        if (string.Equals(s, d, IgnoreCase))
            return ApiError.Of("move.sameFolder");
        if (d.StartsWith(s + Path.DirectorySeparatorChar, IgnoreCase))
            return ApiError.Of("move.insideCurrent");
        if (File.Exists(d))
            return ApiError.Of("path.isFile");
        // El destino puede ser la raíz de otro disco, con sus juegos (#208): solo no puede tener datos de Otteretro.
        if (DataFolder.Entries.Any(e => Path.Exists(Path.Combine(d, e))))
            return ApiError.Of("move.destinationNotEmpty");
        if (!Mounts.Same(s, d))
        {
            var root = Mounts.Of(d);
            if (!Directory.Exists(root)) return ApiError.Of("move.driveNotFound", ("drive", root));
            try
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                var needed = Size(s);
                if (free < needed)
                    return ApiError.Of("move.notEnoughSpace", ("drive", root), ("needed", needed.ToString()), ("free", free.ToString()));
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Rutas de red (\\nas\carpeta) o discos sin acceso.
                return ApiError.Of("move.driveUnavailable", ("drive", root), ("detail", e.Message));
            }
        }
        return null;
    }

    /// <summary>Copia (o mueve, en el mismo disco) y verifica. El origen sigue intacto salvo en el movimiento del mismo disco.</summary>
    /// <param name="forceCopy">Forzar la copia aunque sea el mismo disco (las pruebas lo usan para probar la copia).</param>
    public static void CopyAndVerify(string source, string destination, Action<MoveProgress> report, bool forceCopy = false)
    {
        CloseDatabase(source);

        var entries = Entries(source).ToList();
        if (!forceCopy && Mounts.Same(source, destination))
        {
            report(new("moving", 0, 1));
            Directory.CreateDirectory(destination);
            foreach (var entry in entries) Directory.Move(entry, Path.Combine(destination, Path.GetFileName(entry)));
            report(new("moving", 1, 1));
            return;
        }

        var files = entries.SelectMany(e => Directory.EnumerateFiles(e, "*", SearchOption.AllDirectories))
            .Select(f => (relative: Path.GetRelativePath(source, f), size: new FileInfo(f).Length))
            .ToList();
        var total = files.Sum(f => f.size);
        try
        {
            foreach (var folder in entries.SelectMany(e => Directory.EnumerateDirectories(e, "*", SearchOption.AllDirectories).Prepend(e)))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, folder)));

            long done = 0;
            foreach (var (relative, size) in files)
            {
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(source, relative), target);
                FlushToDisk(target);
                report(new("copying", done += size, total));
            }

            done = 0;
            foreach (var (relative, size) in files)
            {
                if (!SameContent(Path.Combine(source, relative), Path.Combine(destination, relative)))
                    throw new CopyMismatchException(relative);
                report(new("verifying", done += size, total));
            }
        }
        catch
        {
            try { Undo(source, destination); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Deja todo como antes de <see cref="CopyAndVerify"/>: si se movió (mismo disco) la devuelve a su sitio;
    /// si se copió, borra del destino solo lo copiado y las carpetas que quedan vacías, nunca otra cosa.
    /// </summary>
    public static void Undo(string source, string destination)
    {
        if (!Directory.Exists(destination)) return;
        foreach (var name in DataFolder.Entries)
        {
            var (from, to) = (Path.Combine(source, name), Path.Combine(destination, name));
            if (!Directory.Exists(to)) continue;
            if (!Directory.Exists(from))
            {
                Directory.CreateDirectory(source);
                Directory.Move(to, from); // se había movido (mismo disco)
                continue;
            }
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(destination, Path.GetRelativePath(source, f));
                if (!File.Exists(copy)) continue;
                File.SetAttributes(copy, FileAttributes.Normal);
                File.Delete(copy);
            }
            foreach (var c in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).Prepend(from)
                         .Select(c => Path.Combine(destination, Path.GetRelativePath(source, c))).OrderByDescending(c => c.Length))
                if (Directory.Exists(c) && !Directory.EnumerateFileSystemEntries(c).Any())
                    Directory.Delete(c);
        }
        if (!Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination);
    }

    /// <summary>Lo de la carpeta de datos que existe en <paramref name="folder"/> (#208).</summary>
    static IEnumerable<string> Entries(string folder) =>
        DataFolder.Entries.Select(e => Path.Combine(folder, e)).Where(Directory.Exists);

    /// <summary>Obliga a Windows a escribir el archivo en el disco: sin esto, la verificación puede leer la caché y se borraría el origen antes de tiempo.</summary>
    internal static void FlushToDisk(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write))
            f.Flush(flushToDisk: true);
        File.SetAttributes(path, attributes);
    }

    /// <summary>Borra la carpeta anterior. Si algo queda bloqueado, devuelve un aviso en vez de fallar: los datos ya están en el destino.</summary>
    public static ApiError? DeleteSource(string source)
    {
        if (!Directory.Exists(source)) return null;
        try
        {
            // Solo lo de los datos: los juegos y herramientas del disco se quedan (#208). La carpeta, si quedó vacía.
            foreach (var entry in Entries(source)) Directory.Delete(entry, true);
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ApiError.Of("move.sourceNotDeleted", ("path", source), ("detail", e.Message));
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime At)> sizes = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> measuring = new();

    /// <summary>
    /// Lo que ocupa la carpeta según la última medición, o null si todavía no hay ninguna (#106, #177). Recorrerla tarda un
    /// segundo con 25.000 portadas: se mide por detrás, al arrancar y cuando la medición tiene más de un minuto.
    /// </summary>
    public static long? RememberedSize(string folder)
    {
        (long Size, DateTime At)? known = sizes.TryGetValue(folder, out var s) ? s : null;
        if (known is null || DateTime.UtcNow - known.Value.At > TimeSpan.FromMinutes(1))
            measuring.GetOrAdd(folder, f => Task.Run(() =>
            {
                try { sizes[f] = (Size(f), DateTime.UtcNow); }
                catch (IOException) { } // un archivo que se borró mientras se medía: se vuelve a medir en el próximo pedido
                catch (UnauthorizedAccessException) { }
                finally { measuring.TryRemove(f, out _); }
            }));
        return known?.Size;
    }

    /// <summary>Lo que ocupan los datos de la carpeta (#208), sin los juegos ni las herramientas del disco.</summary>
    public static long Size(string folder) =>
        Entries(folder).SelectMany(e => Directory.EnumerateFiles(e, "*", SearchOption.AllDirectories)).Sum(f => new FileInfo(f).Length);

    /// <summary>Vuelca el WAL a la base y suelta las conexiones, para copiar o mover el archivo sin que esté en uso.</summary>
    static void CloseDatabase(string folder)
    {
        foreach (var path in (string[])[DataFolder.DatabasePath(folder), DataFolder.CatalogPath(folder)])
        {
            if (!File.Exists(path)) continue;
            using var c = new SqliteConnection($"Data Source={path}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    internal static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

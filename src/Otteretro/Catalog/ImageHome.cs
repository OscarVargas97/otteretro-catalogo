using System.Collections.Concurrent;
using System.Text.Json;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="Star">La raíz del disco ★ (`&lt;disco&gt;\Otteretro`), o null si todavía no se eligió.</param>
/// <param name="Folder">Donde van las imágenes: `catalogo\imagenes` del ★.</param>
/// <param name="ManagedByOtteretro">El ★ es la carpeta de datos de Otteretro: se cambia desde Otteretro, no desde aquí.</param>
/// <param name="Available">El ★ está conectado.</param>
public record ImageHomeInfo(string? Star, string? Folder, bool ManagedByOtteretro, bool Available);

/// <param name="Folder">Dónde quedaría el ★ en ese disco.</param>
/// <param name="HasOtteretro">Ya tiene la carpeta de Otteretro con su marcador.</param>
public record StarDisk(string Name, string Folder, bool HasOtteretro, long Free);

/// <summary>
/// Dónde viven las imágenes (#9): no hay carpeta propia. Van en `catalogo\imagenes` del disco ★ de Otteretro, que es su carpeta de
/// datos, y siguen al ★ cuando Otteretro lo cambia. Sin Otteretro, este programa prepara el ★ igual que lo haría Otteretro, para
/// que al instalarlo lo reconozca como suyo. De la configuración de Otteretro solo se lee la ruta de su carpeta de datos.
/// </summary>
public static class ImageHome
{
    /// <summary>La configuración de Otteretro. OTTERETRO_APP_CONFIG la cambia en pruebas y en desarrollo, para no tocar la real.</summary>
    public static string OtteretroConfig { get; set; } = Environment.GetEnvironmentVariable("OTTERETRO_APP_CONFIG") ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otteretro", "config.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "otteretro", "config.json"));

    public const string MarkerFile = "otteretro-ubicacion.json";
    /// <summary>Las carpetas fijas de un disco de Otteretro (#201 de Otteretro).</summary>
    static readonly string[] DiskFolders = ["Juegos", "Emuladores", "Ports", "Complementos", "Sistema"];

    // Junto al catálogo: se mudan con él y nunca entran en lo publicado.
    const string StarFile = "disco-predeterminado.txt", LastFile = "imagenes-ultima.txt";
    static string Beside(Database db, string name) => Path.Combine(Path.GetDirectoryName(db.CatalogPath)!, name);

    /// <summary>Se consulta por cada imagen: se recuerda lo leído hasta que el archivo cambia.</summary>
    static readonly ConcurrentDictionary<string, (DateTime Stamp, string? Value)> read = new();

    static string? Read(string file, Func<string, string?> parse)
    {
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (read.TryGetValue(file, out var hit) && hit.Stamp == stamp) return hit.Value;
        string? value = null;
        try { if (stamp != DateTime.MinValue) value = parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        read[file] = (stamp, value);
        return value;
    }

    /// <summary>La carpeta de datos de Otteretro (su ★), o null si Otteretro no está instalado o no la eligió.</summary>
    public static string? OtteretroDataFolder() => Read(OtteretroConfig, text =>
        JsonDocument.Parse(text).RootElement.TryGetProperty("carpetaDatos", out var p) && p.GetString() is { Length: > 0 } path ? path : null);

    public static ImageHomeInfo Info(Database db)
    {
        var otteretro = OtteretroDataFolder();
        var star = otteretro ?? Read(Beside(db, StarFile), text => text.Trim() is { Length: > 0 } t ? t : null);
        return new(star, star is null ? null : Path.Combine(star, "catalogo", "imagenes"), otteretro is not null, star is not null && Directory.Exists(star));
    }

    public static string? Folder(Database db) => Info(db).Folder;

    /// <summary>Los discos donde se puede poner el ★, con la carpeta que tendría en cada uno.</summary>
    public static List<StarDisk> Disks(IEnumerable<(string Name, long Free)> drives) => drives
        .Select(d => (d.Name, d.Free, Folder: Path.Combine(d.Name, "Otteretro")))
        .Select(d => new StarDisk(d.Name, d.Folder, File.Exists(Path.Combine(d.Folder, MarkerFile)), d.Free))
        .ToList();

    /// <summary>
    /// Sin Otteretro: prepara el ★ como lo hace Otteretro (su marcador de disco y sus carpetas) y lleva ahí las imágenes que había.
    /// Con Otteretro instalado no se puede: el ★ se cambia desde Otteretro.
    /// </summary>
    public static ApiError? SetStar(Database db, string? star)
    {
        if (OtteretroDataFolder() is not null) return ApiError.Of("images.managedByOtteretro");
        if (string.IsNullOrWhiteSpace(star) || !Path.IsPathFullyQualified(star)) return ApiError.Of("path.notAbsolute");
        star = Path.TrimEndingDirectorySeparator(star.Trim());
        try
        {
            Directory.CreateDirectory(star);
            var marker = Path.Combine(star, MarkerFile);
            if (!File.Exists(marker))
            {
                var root = Path.GetPathRoot(star) ?? star;
                var name = Path.TrimEndingDirectorySeparator(root) is { Length: > 0 } n ? n : root;
                // Las mismas claves que escribe Otteretro (en español: las ve la persona).
                File.WriteAllText(marker, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["nombre"] = name,
                    ["tipo"] = star.StartsWith(@"\\", StringComparison.Ordinal) || star.StartsWith("//", StringComparison.Ordinal) ? "network" : "local",
                    ["creada"] = DateTime.UtcNow.ToString("O"),
                    ["formato"] = 1,
                    ["contenido"] = "disk",
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            foreach (var folder in DiskFolders) Directory.CreateDirectory(Path.Combine(star, folder));
            Directory.CreateDirectory(Path.Combine(star, "catalogo", "imagenes"));
            var file = Beside(db, StarFile);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, star);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ApiError.Of("folder.createFailed", ("detail", e.Message));
        }
        Migrate(db);
        return null;
    }

    /// <summary>
    /// Si el ★ cambió desde la última vez, lleva al nuevo las imágenes que quedaron en el anterior (o en la carpeta propia de antes de #9),
    /// sin pisar las que ya están. Cuando Otteretro muda el ★, ya llevó las suyas: el lugar anterior no existe y no hay nada que hacer.
    /// </summary>
    /// <returns>Cuántas imágenes se llevaron.</returns>
    public static int Migrate(Database db)
    {
        var info = Info(db);
        if (!info.Available) return 0;
        var target = info.Folder!;
        var lastFile = Beside(db, LastFile);
        var last = Read(lastFile, text => text.Trim() is { Length: > 0 } t ? t : null) ?? Path.Combine(Path.GetDirectoryName(db.CatalogPath)!, "imagenes");
        var moved = 0;
        if (!Same(last, target) && Directory.Exists(last))
        {
            Directory.CreateDirectory(target);
            // ponytail: de a una y en el momento; si alguna vez hay que llevar decenas de miles, pasarlo a una tarea con avance.
            foreach (var file in Directory.GetFiles(last))
            {
                var to = Path.Combine(target, Path.GetFileName(file));
                if (File.Exists(to)) continue; // lo que ya está en el ★ no se pisa
                File.Move(file, to);
                moved++;
            }
            if (!Directory.EnumerateFileSystemEntries(last).Any()) Directory.Delete(last);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(lastFile)!);
        File.WriteAllText(lastFile, target);
        if (moved > 0) FileLog.Write(LogLevel.Information, "Images", $"{moved} imágenes llevadas de {last} a {target}.");
        return moved;
    }

    static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

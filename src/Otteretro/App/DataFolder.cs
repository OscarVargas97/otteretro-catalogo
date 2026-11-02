using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.App;

/// <summary>
/// La carpeta de datos: lo propio (`data`), el catálogo con sus imágenes (`catalogo`, #167) y logs (P1, S7). Es la raíz
/// `Otteretro` del disco ★ (#208): convive con Juegos, Emuladores, Ports, Complementos y Sistema, que no son suyos.
/// </summary>
public static class DataFolder
{
    // Los nombres de las subcarpetas son datos del usuario: no se traducen.
    public static readonly string[] Subfolders = ["data", Path.Combine("catalogo", "imagenes"), "logs"];

    /// <summary>
    /// Lo que es de la carpeta de datos y se muda con ella (#208): nada más. `Herramientas` y `cache` son de versiones
    /// anteriores (#204, #167) y se llevan si quedaron.
    /// </summary>
    public static readonly string[] Entries = ["data", "catalogo", "logs", "Herramientas", "cache"];

    /// <summary>Crea la carpeta y sus subcarpetas. Devuelve el error o null si quedó lista.</summary>
    public static ApiError? Prepare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return ApiError.Of("path.notAbsolute");
        if (File.Exists(path))
            return ApiError.Of("path.isFile");
        try
        {
            foreach (var sub in Subfolders)
                Directory.CreateDirectory(Path.Combine(path, sub));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ApiError.Of("folder.createFailed", ("detail", e.Message));
        }
        return null;
    }

    /// <summary>Lista si tiene su base; las subcarpetas que falten (de una versión anterior) las crea <see cref="Upgrade"/>.</summary>
    public static bool IsReady(string? path) => path is not null && Directory.Exists(Path.Combine(path, "data"));

    /// <summary>
    /// Pone al día una carpeta de una versión anterior: crea lo que falte y pasa las imágenes de `cache/imagenes` al catálogo
    /// (#167). Es la misma unidad, así que se renombra la carpeta: no se copia nada.
    /// </summary>
    public static void Upgrade(string folder)
    {
        var old = Path.Combine(folder, "cache", "imagenes");
        var images = Path.Combine(folder, "catalogo", "imagenes");
        if (Directory.Exists(old) && !(Directory.Exists(images) && Directory.EnumerateFileSystemEntries(images).Any()))
        {
            Directory.CreateDirectory(Path.Combine(folder, "catalogo"));
            if (Directory.Exists(images)) Directory.Delete(images);
            Directory.Move(old, images);
            if (!Directory.EnumerateFileSystemEntries(Path.Combine(folder, "cache")).Any()) Directory.Delete(Path.Combine(folder, "cache"));
        }
        Prepare(folder);
    }

    /// <summary>La base de la carpeta: lo propio y el catálogo adjunto.</summary>
    public static Database Open(string folder) => new(DatabasePath(folder), CatalogPath(folder));

    public static string CatalogPath(string folder) => Path.Combine(folder, "catalogo", "catalogo.db");

    public static string DatabasePath(string folder) => Path.Combine(folder, "data", "otteretro.db");
    public static string LogsPath(string folder) => Path.Combine(folder, "logs");
}

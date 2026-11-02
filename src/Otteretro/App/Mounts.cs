using Otteretro.Api;

namespace Otteretro.App;

/// <summary>
/// Discos según el sistema: en Windows, la unidad (C:\); en Linux, el punto de montaje
/// donde cae la ruta (/, /home, /mnt/juegos…). Path.GetPathRoot en Linux siempre da "/".
/// </summary>
public static class Mounts
{
    /// <summary>El disco que contiene la ruta (exista o no todavía).</summary>
    public static string Of(string path) => OperatingSystem.IsWindows()
        ? Path.GetPathRoot(Path.GetFullPath(path))!
        : Of(Path.GetFullPath(path), DriveInfo.GetDrives().Select(d => d.Name));

    /// <summary>El montaje más largo que es prefijo de la ruta (absoluta, con /).</summary>
    public static string Of(string full, IEnumerable<string> mounts) =>
        mounts.Where(m => full == m || full.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal))
            .MaxBy(m => m.Length) ?? "/";

    public static bool Same(string a, string b) =>
        string.Equals(Of(a), Of(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Los discos que se ofrecen para guardar datos. En Linux, solo la raíz y donde se montan
    /// discos de la persona: fuera quedan /boot, /nix/store y los montajes del sistema.
    /// </summary>
    // ponytail: lista fija de prefijos; si aparece otro lugar habitual de montaje, se suma acá.
    public static bool IsUserMount(string mount) => mount == "/" ||
        new[] { "/home/", "/mnt/", "/media/", "/run/media/" }.Any(p => mount == p.TrimEnd('/') || mount.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Los discos que se ofrecen para la carpeta de datos, con la carpeta que propone cada uno y cuál se sugiere.
    /// En Linux quedan fuera los montajes en memoria: dentro de Steam (su entorno FHS) "/" es un tmpfs (#39).
    /// </summary>
    public static List<Drive> Offer(IEnumerable<(string Name, string Format, long Free, long Total)> drives, string home, bool windows)
    {
        var list = drives.Where(d => windows || (IsUserMount(d.Name) && d.Format is not ("tmpfs" or "ramfs"))).ToList();
        if (list.Count == 0) return [];
        // En Linux, ~/Otteretro en el montaje de la carpeta personal (#114); en Windows, el disco con más espacio libre.
        var homeMount = windows ? null : Of(home, list.Select(d => d.Name));
        var suggested = list.Any(d => d.Name == homeMount) ? homeMount : list.MaxBy(d => d.Free).Name;
        return list.Select(d => new Drive(d.Name, d.Free, d.Total,
            d.Name == homeMount ? Path.Combine(home, "OtteretroCatalogo") : Path.Combine(d.Name, "OtteretroCatalogo"),
            d.Name == suggested)).ToList();
    }
}

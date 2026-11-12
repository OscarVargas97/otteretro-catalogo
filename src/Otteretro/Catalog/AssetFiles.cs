using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Otteretro.Catalog;

/// <summary>
/// Qué trae el archivo de una release (otteretro-catalogo#174), para elegir y comprobar el <c>detect</c> de una receta sin bajarlo: de un
/// zip se lee solo su directorio central con pedidos HTTP Range. El criterio es el de Otteretro al instalar (Installs.LocalRecipe): si todo
/// viene en una sola carpeta se entra en ella, y los instaladores (setup, unins, vc_redist…) no son el ejecutable.
/// </summary>
public static partial class AssetFiles
{
    /// <summary>El final de un zip donde está su registro de cierre (22 bytes más un comentario de hasta 65535).</summary>
    const int Tail = 22 + 65_535;
    /// <summary>Un servidor sin Range: hasta este tamaño se baja entero; más grande, no se comprueba.</summary>
    const long WholeMax = 64L << 20;
    const int MaxDirectory = 16 << 20;

    /// <summary>
    /// Lo que no es el juego aunque sea un .exe (otteretro-catalogo#179, docs/reglas-empaquetado.md): instaladores y desinstaladores en
    /// cualquier parte del nombre (TotK-Setup.exe), actualizadores, reportes de fallos, servidores, herramientas de extracción o de ajustes,
    /// y las variantes sin ventana, de consola o de desarrollo.
    /// </summary>
    [GeneratedRegex(@"vc_?redist|unins|setup|install|update|crash|report|serve|relay|headless|settings|shadereditor|extract|probe|capabilit|check|cache|(?<![a-z])ded(?![a-z])|(^|[-_ ])(room|cli|dev)\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex Helper();

    /// <summary>Un instalador por su nombre: si no tiene una firma conocida, igual lo es.</summary>
    [GeneratedRegex("setup|install", RegexOptions.IgnoreCase)]
    private static partial Regex SetupName();

    /// <summary>
    /// Los archivos (rutas con /) como quedan instalados: un zip, por su directorio central y, si todo viene en una sola carpeta, desde ella;
    /// un .exe suelto, él mismo (con <paramref name="saveAs"/>, si la receta lo renombra). Null si no se puede saber: otro formato (7z,
    /// AppImage…), un zip dañado o uno grande en un servidor sin Range. Un error HTTP lanza, como <see cref="Recipes.Api"/>.
    /// </summary>
    public static async Task<List<string>?> List(string name, string url, CancellationToken ct, string? saveAs = null)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return [saveAs ?? name];
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        // El tamaño primero: el CDN de GitHub no acepta un rango del final (bytes=-N, 501).
        if (await Get(url, 0, 1, ct) is not { } got) return null;
        if (got.Bytes.Length < got.Size && await Get(url, Math.Max(0, got.Size - Tail), Math.Min(Tail, got.Size), ct) is { } end) got = end;
        var (tail, start, size) = got;

        async Task<byte[]?> At(long offset, long length)
        {
            if (offset >= start && offset + length <= start + tail.Length) return tail.AsSpan((int)(offset - start), (int)length).ToArray();
            return offset >= 0 && offset + length <= size && await Get(url, offset, length, ct) is { } r && r.Start == offset && r.Bytes.Length >= length ? r.Bytes[..(int)length] : null;
        }

        var eocd = tail.AsSpan().LastIndexOf("PK\x05\x06"u8);
        if (eocd < 0 || eocd + 22 > tail.Length) return null;
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12)), cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            // Zip64: el localizador va justo antes y apunta al registro de cierre de 64 bits.
            if (eocd < 20 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) != 0x07064b50) return null;
            if (await At((long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(eocd - 12)), 56) is not { } z || BinaryPrimitives.ReadUInt32LittleEndian(z) != 0x06064b50) return null;
            (cdSize, cdOffset) = ((long)BinaryPrimitives.ReadUInt64LittleEndian(z.AsSpan(40)), (long)BinaryPrimitives.ReadUInt64LittleEndian(z.AsSpan(48)));
        }
        if (cdSize > MaxDirectory || await At(cdOffset, cdSize) is not { } cd) return null;

        var names = new List<string>();
        for (var p = 0; p + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) == 0x02014b50;)
        {
            var utf8 = (BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 8)) & 0x800) != 0;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 28)), extra = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 30)),
                comment = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 32));
            if (p + 46 + n > cd.Length) return null;
            // shortcut: sin la marca UTF-8 el nombre es CP437; se lee como Latin-1, que coincide en ASCII (los nombres de los .exe).
            names.Add((utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(cd, p + 46, n).Replace('\\', '/'));
            p += 46 + n + extra + comment;
        }
        var files = names.Where(f => f.Length > 0 && !f.EndsWith('/')).ToList();
        // Todo dentro de una sola carpeta: se entra en ella, como Otteretro al extraer.
        if (files.Count > 0 && files.All(f => f.Contains('/')) && files.Select(f => f[..f.IndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            files = files.Select(f => f[(f.IndexOf('/') + 1)..]).ToList();
        return files;
    }

    /// <summary>
    /// Un pedido Range. Devuelve los bytes, desde dónde empiezan y el tamaño del archivo. Un servidor que responde entero: si no es
    /// grande, sirve igual; si lo es, null.
    /// </summary>
    static async Task<(byte[] Bytes, long Start, long Size)?> Get(string url, long from, long length, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, from + length - 1);
        using var response = await Recipes.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)response.StatusCode}", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { From: { } start, Length: { } size })
            return (await response.Content.ReadAsByteArrayAsync(ct), start, size);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is not (> 0 and <= WholeMax)) return null;
        var all = await response.Content.ReadAsByteArrayAsync(ct);
        return (all, 0, all.Length);
    }

    /// <summary>Los .exe que trae, en cualquier carpeta: la lista que se muestra para elegir.</summary>
    public static List<string> Exes(List<string> files) =>
        files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// El ejecutable claro (docs/reglas-empaquetado.md), entre los .exe que no son ayudantes (<see cref="Helper"/>) de la raíz o, si la raíz
    /// no trae ningún .exe, de las subcarpetas: el único de la raíz; el que se llama como el repo (<paramref name="repoName"/>, sin contar
    /// guiones, espacios ni mayúsculas); o el único que no es un lanzador («Play X.exe», «X Launcher.exe»), porque el lanzador abre ese mismo
    /// juego; en un emulador, en cambio, el lanzador es su interfaz y no se descarta (<paramref name="emulator"/>). Null si no hay uno claro:
    /// lo elige la persona.
    /// </summary>
    public static string? Pick(List<string> files, string repoName, bool emulator = false)
    {
        var all = files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var exes = all.Where(f => !Helper().IsMatch(f[(f.LastIndexOf('/') + 1)..])).ToList();
        var root = exes.Where(f => !f.Contains('/')).ToList();
        if (root is [var only]) return only;
        var pool = root.Count > 0 ? root : all.Any(f => !f.Contains('/')) ? [] : exes; // un instalador en la raíz: lo de adentro es suyo
        bool Named(string f) => Key(Path.GetFileNameWithoutExtension(f)) == Key(repoName);
        if (pool.Where(Named).ToList() is [var named]) return named;
        return !emulator && pool.Count > 1 && pool.Where(f => !Launcher().IsMatch(f[(f.LastIndexOf('/') + 1)..])).ToList() is [var game] ? game : null;
    }

    [GeneratedRegex("launcher|^play ", RegexOptions.IgnoreCase)]
    private static partial Regex Launcher();

    static string Key(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// Si un .exe es un instalador y con qué se armó: inno, nsis, 7zsfx, o «setup» si solo el nombre lo dice. Null si es el juego mismo.
    /// Las firmas van en el cabezal, antes de lo empaquetado: basta con los primeros 2 MB.
    /// </summary>
    public static async Task<string?> Packer(string name, string url, CancellationToken ct)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var head = await Get(url, 0, 2 << 20, ct) is { } got ? got.Bytes : Array.Empty<byte>();
        if (head.AsSpan().IndexOf("Inno Setup"u8) >= 0) return "inno";
        if (head.AsSpan().IndexOf("NullsoftInst"u8) >= 0) return "nsis";
        if (head.AsSpan().IndexOf(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }) >= 0) return "7zsfx";
        return SetupName().IsMatch(name) ? "setup" : null;
    }

    /// <summary>
    /// Los argumentos para instalar sin preguntar en {dir}, si se conocen: los de Inno Setup. NSIS pide /D= al final y sin comillas, y
    /// Otteretro pone comillas a un argumento con espacios (otteretro#445); los demás no tienen argumentos comunes.
    /// </summary>
    public static List<string>? InstallerArgs(string? packer) =>
        packer == "inno" ? ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOICONS", "/DIR={dir}"] : null;

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex Version();

    /// <summary>Lo que una receta nueva saca de su archivo de Windows según las reglas de empaquetado (docs/reglas-empaquetado.md).</summary>
    /// <param name="Exe">El ejecutable claro, o null si hay que elegirlo o no se puede saber.</param>
    /// <param name="Files">Lo que trae el archivo; null si no se puede saber (un 7z, un instalador).</param>
    /// <param name="Packer">Si es un instalador, con qué se armó (<see cref="Packer"/>).</param>
    /// <param name="Installer">Los argumentos del instalador, si se conocen.</param>
    /// <param name="SaveAs">Un .exe suelto con la versión en el nombre se guarda con el del repo, que no cambia entre versiones.</param>
    public sealed record Resolved(string? Exe, List<string>? Files, string? Packer, List<string>? Installer, string? SaveAs);

    /// <summary>
    /// Aplica las reglas a un archivo: un zip, por lo que trae (<see cref="Pick"/>); un .exe, por su firma: instalador (con sus argumentos si
    /// son de Inno) o el juego mismo. Un error de red queda como «no se puede saber».
    /// </summary>
    public static async Task<Resolved> Resolve(string name, string url, string repoName, bool emulator, CancellationToken ct)
    {
        try
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (await Packer(name, url, ct) is { } packer) return new(null, null, packer, InstallerArgs(packer), null);
                var saveAs = Version().IsMatch(name) ? repoName + ".exe" : null;
                return new(saveAs ?? name, [saveAs ?? name], null, null, saveAs);
            }
            var files = await List(name, url, ct);
            return new(files is null ? null : Pick(files, repoName, emulator), files, null, null, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(null, null, null, null, null);
        }
    }

    /// <summary>Si trae el <c>detect</c> de la receta (una ruta relativa a la instalación, como la busca Otteretro).</summary>
    public static bool Has(List<string> files, string detect) =>
        files.Contains(detect.Replace('\\', '/').TrimStart('.', '/'), StringComparer.OrdinalIgnoreCase);
}

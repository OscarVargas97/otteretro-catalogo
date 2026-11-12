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

    /// <summary>Los mismos que en Otteretro (Installs.Installer).</summary>
    [GeneratedRegex(@"^(vc_?redist|unins|setup|install|crashpad|updater)", RegexOptions.IgnoreCase)]
    private static partial Regex Installer();

    /// <summary>
    /// Los archivos (rutas con /) como quedan instalados: un zip, por su directorio central y, si todo viene en una sola carpeta, desde ella;
    /// un .exe suelto, él mismo (con <paramref name="saveAs"/>, si la receta lo renombra). Null si no se puede saber: otro formato (7z,
    /// AppImage…), un zip dañado o uno grande en un servidor sin Range. Un error HTTP lanza, como <see cref="Recipes.Api"/>.
    /// </summary>
    public static async Task<List<string>?> List(string name, string url, CancellationToken ct, string? saveAs = null)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return [saveAs ?? name];
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        if (await Get(url, null, Tail, ct) is not { } got) return null;
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
    /// Un pedido Range (<paramref name="from"/> null: los últimos <paramref name="length"/> bytes). Devuelve los bytes, desde dónde
    /// empiezan y el tamaño del archivo. Un servidor que responde entero: si no es grande, sirve igual; si lo es, null.
    /// </summary>
    static async Task<(byte[] Bytes, long Start, long Size)?> Get(string url, long? from, long length, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = from is { } f ? new RangeHeaderValue(f, f + length - 1) : new RangeHeaderValue(null, length);
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
    /// El ejecutable claro: el único .exe de la raíz que no es un instalador o, entre varios, el que se llama como el repo
    /// (<paramref name="repoName"/>). Null si no hay uno claro.
    /// </summary>
    public static string? Pick(List<string> files, string repoName) =>
        files.Where(f => !f.Contains('/') && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Installer().IsMatch(f)).ToList() is var root && root is [var only]
            ? only : root.FirstOrDefault(f => f.Equals(repoName + ".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Si trae el <c>detect</c> de la receta (una ruta relativa a la instalación, como la busca Otteretro).</summary>
    public static bool Has(List<string> files, string detect) =>
        files.Contains(detect.Replace('\\', '/').TrimStart('.', '/'), StringComparer.OrdinalIgnoreCase);
}

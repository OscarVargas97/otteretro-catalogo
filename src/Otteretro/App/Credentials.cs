using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Otteretro.Data;

namespace Otteretro.App;

/// <summary>Un dato que se guarda cifrado: servicio, campo, dónde se consigue (§5.6) y la fase en que la app empieza a usarlo.</summary>
public record CredentialKind(string Service, string Field, string Url, int Phase)
{
    public string Id => $"{Service}.{Field}";
}

/// <summary>Lo que ve Ajustes › Cuentas y claves: nunca el valor, solo si está guardado (#148).</summary>
public record CredentialStatus(string Id, string Service, string Field, string Url, int Phase, bool Saved, string? UpdatedAt);

/// <summary>Sin llavero (Secret Service) en Linux no hay dónde guardar la llave maestra.</summary>
public sealed class KeyringUnavailableException(string detail) : Exception(detail);

/// <summary>Claves y tokens de este equipo, cifrados en la tabla credential.</summary>
public sealed class Credentials(Database db, string machineId, SecretBox box)
{
    // ponytail: las tiendas de PC se suman cuando su fase diga cuáles son y qué piden.
    public static readonly CredentialKind[] Kinds =
    [
        new("github", "token", "https://github.com/settings/personal-access-tokens", 5),
        new("igdb", "clientId", "https://dev.twitch.tv/console/apps", 1),
        new("igdb", "clientSecret", "https://dev.twitch.tv/console/apps", 1),
        new("steamgriddb", "apiKey", "https://www.steamgriddb.com/profile/preferences/api", 1),
        new("rawg", "apiKey", "https://rawg.io/apidocs", 1),
    ];

    public static bool IsKnown(string id) => Kinds.Any(k => k.Id == id);

    public List<CredentialStatus> List()
    {
        using var c = db.Open();
        var saved = c.Query<(string Id, string UpdatedAt)>("SELECT id, updated_at FROM credential WHERE machine_id = @machineId", new { machineId })
            .ToDictionary(r => r.Id, r => r.UpdatedAt);
        return Kinds.Select(k => new CredentialStatus(k.Id, k.Service, k.Field, k.Url, k.Phase, saved.ContainsKey(k.Id), saved.GetValueOrDefault(k.Id))).ToList();
    }

    public void Save(string id, string value)
    {
        using var c = db.Open();
        // Una llave maestra nueva solo si este equipo no tiene nada cifrado con la anterior.
        var first = c.ExecuteScalar<long>("SELECT COUNT(*) FROM credential WHERE machine_id = @machineId", new { machineId }) == 0;
        var sealedValue = box.Protect(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(id), mayCreateKey: first);
        c.Execute("""
            INSERT INTO credential (machine_id, id, value, updated_at) VALUES (@machineId, @id, @sealedValue, @date)
            ON CONFLICT(machine_id, id) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """, new { machineId, id, sealedValue, date = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>El valor en claro, o null si no está guardado. Lo usan las integraciones.</summary>
    public string? Read(string id)
    {
        using var c = db.Open();
        var sealedValue = c.ExecuteScalar<byte[]?>("SELECT value FROM credential WHERE machine_id = @machineId AND id = @id", new { machineId, id });
        return sealedValue is null ? null : Encoding.UTF8.GetString(box.Unprotect(sealedValue, Encoding.UTF8.GetBytes(id)));
    }

    public void Delete(string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM credential WHERE machine_id = @machineId AND id = @id", new { machineId, id });
    }
}

/// <summary>
/// Cifra con el almacén del sistema: DPAPI del usuario en Windows; en Linux, AES-GCM con la llave maestra
/// del llavero (#147). <paramref name="linuxKey"/> (recibe si puede crear la llave) solo lo cambian las pruebas.
/// </summary>
public sealed class SecretBox(Func<bool, byte[]>? linuxKey = null)
{
    readonly Lock gate = new();
    byte[]? key;

    // Con cerrojo: dos primeros guardados a la vez no crean dos llaves. Si el llavero falla, se reintenta en el próximo uso.
    byte[] Key(bool mayCreate)
    {
        lock (gate) return key ??= (linuxKey ?? LinuxSecrets.MasterKey)(mayCreate);
    }

    public byte[] Protect(byte[] plain, byte[] context, bool mayCreateKey = false) =>
        OperatingSystem.IsWindows() ? WindowsSecrets.Protect(plain, context) : LinuxSecrets.Seal(Key(mayCreateKey), plain, context);

    public byte[] Unprotect(byte[] sealedValue, byte[] context) =>
        OperatingSystem.IsWindows() ? WindowsSecrets.Unprotect(sealedValue, context) : LinuxSecrets.Open(Key(false), sealedValue, context);
}

[SupportedOSPlatform("windows")]
static class WindowsSecrets
{
    public static byte[] Protect(byte[] plain, byte[] context) => ProtectedData.Protect(plain, context, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] sealedValue, byte[] context) => ProtectedData.Unprotect(sealedValue, context, DataProtectionScope.CurrentUser);
}

/// <summary>AES-GCM (nonce + tag + cifrado) y la llave maestra en el llavero con secret-tool (libsecret).</summary>
public static class LinuxSecrets
{
    static readonly string[] Attributes = ["application", "otteretro", "purpose", "credentials"];

    public static byte[] Seal(byte[] key, byte[] plain, byte[] context)
    {
        var result = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16), context);
        return result;
    }

    public static byte[] Open(byte[] key, byte[] sealedValue, byte[] context)
    {
        var plain = new byte[sealedValue.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedValue.AsSpan(0, 12), sealedValue.AsSpan(28), sealedValue.AsSpan(12, 16), plain, context);
        return plain;
    }

    /// <summary>La lee del llavero o, si <paramref name="create"/>, la crea y la guarda ahí.</summary>
    public static byte[] MasterKey(bool create)
    {
        var (found, output, lookupError) = SecretTool(["lookup", .. Attributes], null);
        if (found == 0 && output.Trim().Length > 0) return Convert.FromBase64String(output.Trim());
        // secret-tool falla igual si no hay llave que si el llavero no respondió (bloqueado, aviso cancelado).
        // Con claves ya guardadas, una llave nueva las dejaría ilegibles: se avisa que falta el llavero.
        if (!create) throw new KeyringUnavailableException(lookupError.Trim() is { Length: > 0 } e ? e : "sin llave maestra en el llavero");
        var key = RandomNumberGenerator.GetBytes(32);
        var (stored, _, error) = SecretTool(["store", "--label=Otteretro", .. Attributes], Convert.ToBase64String(key));
        if (stored != 0) throw new KeyringUnavailableException(error.Trim());
        return key;
    }

    /// <summary>El secreto va por la entrada estándar, nunca en los argumentos.</summary>
    static (int Code, string Output, string Error) SecretTool(string[] args, string? input)
    {
        var info = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info)!;
            if (input is not null) p.StandardInput.Write(input);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            // Un llavero bloqueado pide la contraseña: se le da tiempo a la persona.
            if (!p.WaitForExit(TimeSpan.FromMinutes(2))) { p.Kill(); throw new KeyringUnavailableException("secret-tool no respondió"); }
            return (p.ExitCode, output.Result, error.Result);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new KeyringUnavailableException($"secret-tool: {e.Message}");
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <summary>
/// No pagar dos veces (F2.4, §11.2): cada resultado de Claude guarda la huella de los datos con los que se pidió.
/// Quien arma un lote pregunta <see cref="Changed"/> por cada saga (o lo que sea), manda a Claude solo lo que cambió
/// y, al guardar el resultado, llama a <see cref="Save"/>. «Recalcular» es <see cref="Forget"/>.
/// </summary>
public static class ClaudeFingerprint
{
    /// <param name="prompt">El prompt que produce el resultado (ruta-saga, depurar…).</param>
    /// <param name="key">De qué es el resultado: «saga:12».</param>
    /// <param name="input">Los datos de los que depende; si cambian, cambia la huella.</param>
    public static bool Changed(Database db, string prompt, string key, object input)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT input_hash FROM claude_fingerprint WHERE prompt = @prompt AND key = @key",
            new { prompt, key }) != Hash(input);
    }

    public static void Save(Database db, string prompt, string key, object input)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO claude_fingerprint (prompt, key, input_hash, updated_at) VALUES (@prompt, @key, @hash, @now)
            ON CONFLICT (prompt, key) DO UPDATE SET input_hash = excluded.input_hash, updated_at = excluded.updated_at
            """, new { prompt, key, hash = Hash(input), now = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>«Recalcular»: lo próximo que pida ese prompt (de esa clave, o de todas) va a Claude aunque no haya cambios.</summary>
    public static void Forget(Database db, string prompt, string? key = null)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM claude_fingerprint WHERE prompt = @prompt AND (@key IS NULL OR key = @key)", new { prompt, key });
    }

    static string Hash(object input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, JsonSerializerOptions.Web))));
}

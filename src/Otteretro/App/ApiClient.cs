using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Otteretro.Data;

namespace Otteretro.App;

/// <summary>
/// Cliente HTTP de las fuentes de datos (F1.3): lo que ya se preguntó sale de `api_cache`, y lo que va a la red
/// respeta el ritmo de cada fuente aunque lo pidan varias tareas a la vez.
/// </summary>
public sealed class ApiClient(Database db, HttpMessageHandler? handler = null)
{
    /// <summary>Tiempo mínimo entre dos llamadas a la misma fuente. IGDB admite 4 por segundo; 260 ms deja margen.</summary>
    public static readonly Dictionary<string, TimeSpan> Spacing = new()
    {
        ["igdb"] = TimeSpan.FromMilliseconds(260),
        // ponytail: SteamGridDB no publica su límite; 4 por segundo es prudente. Bajarlo si responde 429.
        ["steamgriddb"] = TimeSpan.FromMilliseconds(250),
    };

    /// <summary>Para las fuentes sin ritmo propio: una por segundo, por si acaso.</summary>
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(1);

    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly ConcurrentDictionary<string, Limiter> Limiters = new();

    readonly HttpClient http = handler is null ? Shared : new HttpClient(handler);

    /// <summary>
    /// Devuelve la respuesta de la caché si sigue vigente; si no, la pide (al ritmo de la fuente) y la guarda por `ttl`.
    /// Una respuesta con error, o correcta pero vacía (#78), no se guarda y lanza <see cref="HttpRequestException"/>.
    /// </summary>
    /// <param name="authorize">Pone las claves en la petición; no forman parte de la clave de la caché.</param>
    public async Task<string> Send(string source, HttpMethod method, string url, string? body, TimeSpan ttl,
        Action<HttpRequestMessage>? authorize = null, CancellationToken ct = default)
    {
        var key = $"{source} {method} {url}\n{body}";
        using (var c = db.Open())
        {
            var cached = c.ExecuteScalar<string?>("SELECT response FROM api_cache WHERE key = @key AND expires_at > @now AND trim(response) <> ''",
                new { key, now = DateTime.UtcNow.ToString("O") });
            if (cached is not null) return cached;
        }

        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body);
        authorize?.Invoke(request);

        await Limiters.GetOrAdd(source, s => new Limiter(Spacing.GetValueOrDefault(s, DefaultSpacing))).Wait(ct);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = new HttpRequestException($"{source} respondió {(int)response.StatusCode}: {text[..Math.Min(text.Length, 200)]}", null, response.StatusCode);
            // La cola reintenta un 429 no antes de lo que pide la fuente.
            if (response.Headers.RetryAfter?.Delta is { } wait) error.Data[TaskQueue.RetryAfterKey] = wait;
            throw error;
        }
        // Todas las fuentes responden JSON: vacía es un corte a medias (#78). Sin código, la cola la reintenta (#59).
        if (string.IsNullOrWhiteSpace(text)) throw new HttpRequestException($"{source} respondió {(int)response.StatusCode} sin contenido");

        using (var c = db.Open())
            c.Execute("INSERT OR REPLACE INTO api_cache (key, response, expires_at) VALUES (@key, @text, @expires)",
                new { key, text, expires = DateTime.UtcNow.Add(ttl).ToString("O") });
        return text;
    }

    /// <summary>Deja pasar una llamada a la vez, separadas al menos por `spacing`.</summary>
    sealed class Limiter(TimeSpan spacing)
    {
        readonly SemaphoreSlim gate = new(1, 1);
        long last = -1;

        public async Task Wait(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (last >= 0)
                {
                    var wait = spacing - Stopwatch.GetElapsedTime(last);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
                last = Stopwatch.GetTimestamp();
            }
            finally { gate.Release(); }
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Otteretro.Api;
using Otteretro.App;

namespace Otteretro.Catalog;

/// <summary>Consultas a IGDB (v4) con la cuenta de Twitch guardada en Ajustes › Cuentas y claves.</summary>
public sealed class Igdb(ApiClient api, Credentials credentials, HttpMessageHandler? handler = null)
{
    public const string ApiUrl = "https://api.igdb.com/v4/";
    public const string TokenUrl = "https://id.twitch.tv/oauth2/token";

    /// <summary>Los datos de IGDB cambian poco: una semana en caché.</summary>
    static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    static (string ClientId, string Token, DateTime Expires)? token;

    readonly HttpClient http = handler is null ? Shared : new HttpClient(handler);

    public async Task<T[]> Query<T>(string endpoint, string body, CancellationToken ct) =>
        JsonSerializer.Deserialize<T[]>(await Post(endpoint, body, ct), Json) ?? [];

    /// <summary>Cuántos resultados da `where …;` en ese endpoint (para el progreso).</summary>
    public async Task<long> Count(string endpoint, string where, CancellationToken ct) =>
        JsonDocument.Parse(await Post(endpoint + "/count", where, ct)).RootElement.GetProperty("count").GetInt64();

    async Task<string> Post(string endpoint, string body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (clientId, bearer) = await Token(ct);
            try
            {
                return await api.Send("igdb", HttpMethod.Post, ApiUrl + endpoint, body, Ttl, r =>
                {
                    r.Headers.Add("Client-ID", clientId);
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }, ct);
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                token = null; // el token venció o se revocó: se pide otro una vez
            }
        }
    }

    /// <summary>Token de aplicación de Twitch; se guarda en memoria hasta que vence. Nunca va a la caché ni al log.</summary>
    async Task<(string ClientId, string Token)> Token(CancellationToken ct)
    {
        var clientId = credentials.Read("igdb.clientId");
        var secret = credentials.Read("igdb.clientSecret");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret)) throw new TaskException(ApiError.Of("tasks.igdb.noKey"));
        if (token is { } t && t.ClientId == clientId && t.Expires > DateTime.UtcNow) return (clientId, t.Token);

        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["grant_type"] = "client_credentials",
        }), ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new TaskException(ApiError.Of("tasks.igdb.badKey"));
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        var value = json.GetProperty("access_token").GetString()!;
        token = (clientId, value, DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt64() - 300));
        return (clientId, value);
    }
}

public record IgdbNamed(long Id, string? Name, string? Slug = null, string? Comment = null);
public record IgdbReleaseDate(long? Platform, long? Date);
public record IgdbExternal(string? Uid, int? Category, long? ExternalGameSource);

/// <summary>Un juego de IGDB con lo que usa el importador. `GameType` reemplaza a `Category` (obsoleto) en IGDB.</summary>
public record IgdbGame(
    long Id, string Name, long? FirstReleaseDate, int? GameType, int? Category, long[]? Platforms, long? VersionParent,
    long[]? Remakes, long[]? Remasters, long[]? Ports, IgdbNamed[]? Franchises, IgdbNamed[]? Collections,
    IgdbNamed[]? AlternativeNames, IgdbReleaseDate[]? ReleaseDates, IgdbExternal[]? ExternalGames,
    IgdbNamed? Franchise = null, long? ParentGame = null, string? Summary = null, long? TotalRatingCount = null)
{
    public const string Fields = "fields name, first_release_date, game_type, category, platforms, version_parent, remakes, remasters, ports, "
        + "franchises.name, collections.name, alternative_names.name, alternative_names.comment, release_dates.platform, release_dates.date, "
        + "external_games.uid, external_games.category, external_games.external_game_source, franchise.name, parent_game, summary, total_rating_count;";

    /// <summary>Juego principal, expansión independiente, remake, remaster, juego ampliado o port. Fuera: DLC, packs, mods, episodios…</summary>
    public static readonly int[] PlayableTypes = [0, 4, 8, 9, 10, 11];

    public int Type => GameType ?? Category ?? 0;

    /// <summary>Juego ampliado (10) o port (11): con `parent_game` es una versión de ese juego.</summary>
    public long? EditionOf => Type is 10 or 11 ? ParentGame : null;

    /// <summary>Salió o tiene fecha anunciada. Sin ninguna fecha: cancelado, rumor o prototipo.</summary>
    public bool HasDate => FirstReleaseDate is not null;
}

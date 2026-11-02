using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="RefreshedAt">Hasta cuándo trajo IGDB sus juegos (F2.14); null = nunca se cargó.</param>
/// <param name="Loading">El avance (0 a 100) de su carga desde IGDB, si está en la cola o corriendo (#109).</param>
public record ConsoleInfo(string Id, string Name, string? Company, bool Enabled, string? RefreshedAt, int Games, int? Loading);

/// <param name="RefreshDays">Cada cuántos días se actualiza el catálogo (F2.14).</param>
public record ConsolesPage(List<ConsoleInfo> Consoles, int RefreshDays, string? LastRefresh);

/// <param name="ConsoleId">La consola donde ya está, si está.</param>
public record PlatformInfo(long IgdbId, string Name, string? Abbreviation, string? Company, string? ConsoleId);

public record EnableConsoleRequest(long IgdbId);

public record ConsoleUpdateRequest(bool Enabled);

public record RefreshSettingsRequest(int Days);

/// <summary>Las consolas que habilitas (F2.13, #83) y cada cuánto se actualiza el catálogo (F2.14, #84).</summary>
public static class Consoles
{
    public static ConsolesPage Page(Database db)
    {
        using var c = db.Open();
        var consoles = c.Query<(string Id, string Name, string? Company, bool Enabled, string? RefreshedAt, long Games, long? Loading)>("""
            SELECT k.id, k.name, k.company, k.enabled, k.refreshed_at, (SELECT count(DISTINCT game_id) FROM version v WHERE v.platform = k.id),
                   (SELECT t.progress FROM task t WHERE t.kind = 'igdb.platform' AND t.status IN ('queued', 'running')
                    AND json_extract(t.data, '$.platform') = k.id ORDER BY t.id DESC LIMIT 1)
            FROM console k ORDER BY k.rowid
            """).Select(r => new ConsoleInfo(r.Id, r.Name, r.Company, r.Enabled, r.RefreshedAt, (int)r.Games, (int?)r.Loading)).ToList();
        var settings = c.Query<(string Name, string Value)>("SELECT name, value FROM setting WHERE machine_id = '' AND name LIKE 'catalog.%'")
            .ToDictionary(r => r.Name, r => r.Value);
        return new ConsolesPage(consoles, settings.TryGetValue(IgdbImport.RefreshDays, out var d) && int.TryParse(d, out var n) ? n : 7,
            settings.GetValueOrDefault(IgdbImport.LastRefresh));
    }

    record IgdbFamily(string? Name);
    record IgdbPlatform(long Id, string Name, string? Abbreviation, string? Slug, IgdbFamily? PlatformFamily);

    /// <summary>Todas las plataformas de IGDB (una semana en caché), con la consola donde ya están.</summary>
    public static async Task<List<PlatformInfo>> Available(Database db, Igdb igdb, CancellationToken ct)
    {
        var platforms = await igdb.Query<IgdbPlatform>("platforms", "fields name, abbreviation, slug, platform_family.name; sort name asc; limit 500;", ct);
        Dictionary<long, string> mapped;
        using (var c = db.Open()) mapped = c.Query<(long, string)>("SELECT igdb_id, console_id FROM console_platform").ToDictionary(r => r.Item1, r => r.Item2);
        return platforms.Select(p => new PlatformInfo(p.Id, p.Name, p.Abbreviation, Company(p.PlatformFamily?.Name), mapped.GetValueOrDefault(p.Id))).ToList();
    }

    /// <summary>La compañía, para la parte de la carga de Claude (Nintendo, Sony, Microsoft; las demás van al final).</summary>
    static string? Company(string? family) => family switch
    {
        "Nintendo" => "Nintendo",
        "PlayStation" => "Sony",
        "Xbox" => "Microsoft",
        "Sega" => "Sega",
        _ => null,
    };

    /// <summary>
    /// Habilita la plataforma de IGDB: si ya es parte de una consola, la habilita; si no, crea la consola. Encola la carga de
    /// sus juegos (salvo PC, que va por saga). Devuelve la consola, o null si IGDB no tiene esa plataforma.
    /// </summary>
    public static async Task<string?> Enable(Database db, Igdb igdb, long igdbId, CancellationToken ct)
    {
        string? console;
        using (var c = db.Open()) console = c.ExecuteScalar<string?>("SELECT console_id FROM console_platform WHERE igdb_id = @igdbId", new { igdbId });
        if (console is null)
        {
            var found = (await igdb.Query<IgdbPlatform>("platforms", $"fields name, abbreviation, slug, platform_family.name; where id = {igdbId};", ct)).FirstOrDefault();
            if (found is null) return null;
            console = found.Slug ?? $"igdb-{igdbId}";
            using var c = db.Open();
            using var t = c.BeginTransaction();
            c.Execute("INSERT INTO console (id, name, company) VALUES (@console, @Name, @company) ON CONFLICT (id) DO NOTHING",
                new { console, found.Name, company = Company(found.PlatformFamily?.Name) }, t);
            c.Execute("INSERT INTO console_platform (igdb_id, console_id) VALUES (@igdbId, @console)", new { igdbId, console }, t);
            t.Commit();
        }
        SetEnabled(db, console, true);
        return console;
    }

    /// <summary>Habilitar encola la carga de sus juegos (si no hay una esperando); deshabilitar no borra nada, solo deja de cargarla.</summary>
    public static bool SetEnabled(Database db, string console, bool enabled)
    {
        using (var c = db.Open())
            if (c.Execute("UPDATE console SET enabled = @enabled WHERE id = @console", new { console, enabled }) == 0) return false;
        if (enabled && !IgdbImport.PcPlatforms.Contains(console))
        {
            using var c = db.Open();
            var waiting = c.ExecuteScalar<long>("""
                SELECT count(*) FROM task WHERE kind = 'igdb.platform' AND status IN ('queued', 'running') AND json_extract(data, '$.platform') = @console
                """, new { console }) > 0;
            if (!waiting) IgdbImport.EnqueuePlatform(db, console);
        }
        return true;
    }

    public static ApiError? SaveRefreshDays(Database db, int days)
    {
        if (days is < 1 or > 365) return ApiError.Of("catalog.badRefreshDays");
        using var c = db.Open();
        Claude.ClaudeBudget.Set(c, IgdbImport.RefreshDays, days.ToString());
        return null;
    }
}

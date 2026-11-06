using System.Globalization;
using System.Text.Json;
using Dapper;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="Id">igdb, codes, ports, portSearch, cleanup, routes, iconic, best, translate o images.</param>
/// <param name="Model">El modelo de Claude que usa; null si es un script.</param>
/// <param name="Auto">Si corre sola en el horario (decisión 284).</param>
/// <param name="Days">Solo IGDB: cada cuántos días se pone al día.</param>
/// <param name="LastStatus">queued, running, paused, done, error o canceled de su última ejecución; null si nunca corrió.</param>
/// <param name="MonthCalls">Llamadas a Claude de este mes (decisión 285).</param>
/// <param name="Pending">Cuánto le falta (juegos, versiones, sagas o ports según la tarea; 0 = al día); null si no tiene un pendiente contable.</param>
/// <param name="DueAt">Solo IGDB: cuándo le toca según su intervalo (pasada o hoy = toca ya).</param>
public record CatalogTaskInfo(string Id, string? Model, bool Auto, int? Days, string? LastStatus, string? LastAt, int MonthCalls, double MonthCost,
    int? Pending = null, string? DueAt = null);

public record CatalogTaskRequest(bool? Auto = null, int? Days = null);

/// <summary>
/// Ajustes › Tareas (#58, decisiones 283 a 285): cada tarea del catálogo por separado, en el orden en que corre de noche. Las de
/// Claude son los pasos que antes iban juntos en `claude.load`; cada una salta lo que Claude ya vio (huella) o espera en Revisar.
/// </summary>
public static class CatalogTasks
{
    /// <param name="Prompts">Los prompts de Claude que usa: para su gasto del mes y para reconocer sus ejecuciones.</param>
    /// <param name="Count">Lo que le falta, con el mismo criterio de «falta» que lo que encola; null si no tiene un pendiente contable.</param>
    record Def(string Id, string? Model, string[] Prompts, Func<Database, string?, long?> Enqueue, Func<string, string?, bool> Is, Func<Database, int>? Count = null);

    static Def Step(string id, string prompt, string model) =>
        new(id, model, [prompt], (db, _) => SagaAnalysis.EnqueueStep(db, prompt), (kind, data) => kind == SagaAnalysis.Kind && SagaAnalysis.IsStep(data, prompt),
            db => SagaAnalysis.CountPending(db, prompt));

    static int Scalar(Database db, string sql)
    {
        using var c = db.Open();
        return (int)c.ExecuteScalar<long>(sql);
    }

    /// <summary>
    /// Todas, en el orden de la noche: IGDB → códigos → ports → buscar ports → depurar → icónico → rutas → mejor versión → traducir → imágenes. Icónico va
    /// antes que rutas porque historia y comunidad se piden solo para las icónicas (#54).
    /// </summary>
    static readonly Def[] All =
    [
        new("igdb", null, [], (db, _) => IgdbImport.EnqueueRefresh(db), (kind, _) => kind == "igdb.refresh"),
        new("codes", null, [], (db, _) => GameCodes.Enqueue(db), (kind, _) => kind == GameCodes.Kind, GameCodes.Missing),
        // Ports (#69): la lista y su cruce con los juegos, después de IGDB; la búsqueda de nuevos solo pide a Claude lo dudoso.
        new("ports", null, [], (db, _) => Ports.Enqueue(db), (kind, _) => kind == Ports.Kind,
            db => Scalar(db, "SELECT count(*) FROM port WHERE game_id IS NULL")), // «ports sin juego», como Ports.Page
        new("portSearch", PortSearch.Model, [PortSearch.PromptName], (db, _) => PortSearch.Enqueue(db), (kind, _) => kind == PortSearch.Kind),
        new("cleanup", "haiku", [CatalogCleanup.PromptName], (db, _) => CatalogCleanup.Enqueue(db), (kind, _) => kind == CatalogCleanup.Kind),
        // Con consola, «Reevaluar sagas icónicas» (F2.15): olvida lo dicho de esas sagas y lo vuelve a pedir.
        new("iconic", "sonnet", [SagaAnalysis.Iconic], (db, console) => console is null ? SagaAnalysis.EnqueueStep(db, SagaAnalysis.Iconic) : SagaAnalysis.EnqueueIconic(db, console),
            (kind, data) => kind == SagaAnalysis.Kind && SagaAnalysis.IsStep(data, SagaAnalysis.Iconic),
            db => SagaAnalysis.CountPending(db, SagaAnalysis.Iconic)),
        Step("routes", SagaAnalysis.Routes, "sonnet"),
        Step("best", SagaAnalysis.BestVersion, "sonnet"),
        Step("translate", SagaAnalysis.Translate, "haiku"),
        new("images", null, [], (db, _) => SteamGridDb.Enqueue(db, missing: true), (kind, _) => kind == "sgdb.images",
            // Los mismos juegos que recorre con missing: menos de 3 imágenes.
            db => Scalar(db, "SELECT count(*) FROM game g WHERE (SELECT count(*) FROM catalog.image i WHERE i.game_id = g.id) < 3")),
    ];

    static string Setting(string id) => $"task.auto.{id}";
    static string LastSetting(string id) => $"task.last.{id}";

    /// <summary>La última ejecución de cada tarea (#75, decisión D3): aparte de la cola, así «Limpiar terminadas» no la borra.</summary>
    record LastRun(string Status, string At, double? Seconds, int Progress);

    /// <summary>Al terminar una tarea (hecha, con error o cancelada), la anota como la última de su tarea del catálogo. Una que no es del catálogo no se anota.</summary>
    public static void RecordLast(Database db, long taskId, string status, TimeSpan? elapsed)
    {
        using var c = db.Open();
        if (c.QuerySingleOrDefault<(string Kind, string? Data, long Progress, string? At)>("SELECT kind, data, progress, finished_at FROM task WHERE id = @taskId", new { taskId }) is not { Kind: not null } row) return;
        if (All.FirstOrDefault(t => t.Is(row.Kind, row.Data)) is not { } task) return;
        ClaudeBudget.Set(c, LastSetting(task.Id), JsonSerializer.Serialize(new LastRun(status, row.At ?? DateTime.UtcNow.ToString("O"), elapsed?.TotalSeconds, (int)row.Progress), JsonSerializerOptions.Web));
    }

    public static List<CatalogTaskInfo> List(Database db)
    {
        var now = ClaudeBudget.UtcNow();
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        using var c = db.Open();
        var off = c.Query<string>("SELECT name FROM setting WHERE machine_id = '' AND name LIKE 'task.auto.%' AND value = '0'").ToHashSet();
        var days = c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @RefreshDays", new { IgdbImport.RefreshDays }) is { } d
            && int.TryParse(d, out var n) ? n : 7;
        var usage = c.Query<(string Prompt, long Calls, double Cost)>(
            "SELECT prompt, count(*), COALESCE(SUM(cost_usd), 0.0) FROM claude_usage WHERE created_at >= @month GROUP BY prompt", new { month })
            .ToDictionary(u => u.Prompt);
        var stored = c.Query<(string Name, string Value)>("SELECT name, value FROM setting WHERE machine_id = '' AND name LIKE 'task.last.%'").ToDictionary(x => x.Name, x => x.Value);
        // Lo que está en la cola o corriendo manda sobre la última ejecución guardada.
        var active = c.Query<(string Kind, string? Data, string Status, string At)>(
            "SELECT kind, data, status, created_at FROM task WHERE status IN ('queued', 'running', 'paused') ORDER BY id DESC").ToList();
        return All.Select(t =>
        {
            var last = active.FirstOrDefault(x => t.Is(x.Kind, x.Data)) is { Status: not null } a ? (a.Status, a.At)
                : stored.TryGetValue(LastSetting(t.Id), out var json) && JsonSerializer.Deserialize<LastRun>(json, JsonSerializerOptions.Web) is { } r ? (r.Status, r.At)
                : ((string?)null, (string?)null);
            return new CatalogTaskInfo(t.Id, t.Model, !off.Contains(Setting(t.Id)), t.Id == "igdb" ? days : null, last.Item1, last.Item2,
                (int)t.Prompts.Sum(p => usage.GetValueOrDefault(p).Calls), t.Prompts.Sum(p => usage.GetValueOrDefault(p).Cost),
                t.Count?.Invoke(db), t.Id == "igdb" ? IgdbImport.DueAt(db, days) : null);
        }).ToList();
    }

    /// <summary>«Ejecutar» (decisión 283): la lanza ya, también fuera del horario. False si no existe.</summary>
    public static bool Run(Database db, string id, string? console = null)
    {
        if (All.FirstOrDefault(t => t.Id == id) is not { } task) return false;
        if (Pending(db, task)) return true; // ya está en la cola
        if (task.Model is not null) ClaudeBudget.AllowNow(db);
        task.Enqueue(db, console);
        return true;
    }

    /// <summary>El interruptor «corre sola en el horario» y, en IGDB, cada cuántos días. False si no existe.</summary>
    public static bool Save(Database db, string id, CatalogTaskRequest request)
    {
        if (All.All(t => t.Id != id)) return false;
        using var c = db.Open();
        if (request.Auto is { } auto) ClaudeBudget.Set(c, Setting(id), auto ? "1" : "0");
        if (request.Days is >= 1 and <= 365 && id == "igdb") ClaudeBudget.Set(c, IgdbImport.RefreshDays, request.Days.Value.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// Lo que corre en el horario (decisión 284): cada tarea encendida que no esté ya en la cola, en orden. IGDB solo si pasaron
    /// sus días. La cola corre las de Claude de a una y en orden. Devuelve la primera encolada, o null.
    /// </summary>
    public static long? Nightly(Database db)
    {
        long? first = null;
        foreach (var task in All)
        {
            using (var c = db.Open())
                if (c.ExecuteScalar<string?>("SELECT value FROM setting WHERE machine_id = '' AND name = @name", new { name = Setting(task.Id) }) == "0") continue;
            if (Pending(db, task)) continue;
            var id = task.Id == "igdb" ? IgdbImport.RefreshIfDue(db, ClaudeBudget.UtcNow()) : task.Enqueue(db, null);
            first ??= id;
        }
        return first;
    }

    static bool Pending(Database db, Def task)
    {
        using var c = db.Open();
        return c.Query<(string Kind, string? Data)>("SELECT kind, data FROM task WHERE status IN ('queued', 'running', 'paused')")
            .Any(x => task.Is(x.Kind, x.Data));
    }
}

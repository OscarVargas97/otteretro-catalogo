using System.Globalization;
using Dapper;
using Otteretro.App;
using Otteretro.Claude;
using Otteretro.Data;

namespace Otteretro.Catalog;

/// <param name="Id">igdb, codes, cleanup, routes, iconic, best, translate o images.</param>
/// <param name="Model">El modelo de Claude que usa; null si es un script.</param>
/// <param name="Auto">Si corre sola en el horario (decisión 284).</param>
/// <param name="Days">Solo IGDB: cada cuántos días se pone al día.</param>
/// <param name="LastStatus">queued, running, paused, done, error o canceled de su última ejecución; null si nunca corrió.</param>
/// <param name="MonthCalls">Llamadas a Claude de este mes (decisión 285).</param>
public record CatalogTaskInfo(string Id, string? Model, bool Auto, int? Days, string? LastStatus, string? LastAt, int MonthCalls, double MonthCost);

public record CatalogTaskRequest(bool? Auto = null, int? Days = null);

/// <summary>
/// Ajustes › Tareas (#58, decisiones 283 a 285): cada tarea del catálogo por separado, en el orden en que corre de noche. Las de
/// Claude son los pasos que antes iban juntos en `claude.load`; cada una salta lo que Claude ya vio (huella) o espera en Revisar.
/// </summary>
public static class CatalogTasks
{
    /// <param name="Prompts">Los prompts de Claude que usa: para su gasto del mes y para reconocer sus ejecuciones.</param>
    record Def(string Id, string? Model, string[] Prompts, Func<Database, string?, long?> Enqueue, Func<string, string?, bool> Is);

    static Def Step(string id, string prompt, string model) =>
        new(id, model, [prompt], (db, _) => SagaAnalysis.EnqueueStep(db, prompt), (kind, data) => kind == SagaAnalysis.Kind && SagaAnalysis.IsStep(data, prompt));

    /// <summary>Todas, en el orden de la noche: IGDB → códigos → depurar → rutas → icónico → mejor versión → traducir → imágenes.</summary>
    static readonly Def[] All =
    [
        new("igdb", null, [], (db, _) => IgdbImport.EnqueueRefresh(db), (kind, _) => kind == "igdb.refresh"),
        new("codes", null, [], (db, _) => GameCodes.Enqueue(db), (kind, _) => kind == GameCodes.Kind),
        new("cleanup", "haiku", [CatalogCleanup.PromptName], (db, _) => CatalogCleanup.Enqueue(db), (kind, _) => kind == CatalogCleanup.Kind),
        Step("routes", SagaAnalysis.Routes, "sonnet"),
        // Con consola, «Reevaluar sagas icónicas» (F2.15): olvida lo dicho de esas sagas y lo vuelve a pedir.
        new("iconic", "sonnet", [SagaAnalysis.Iconic], (db, console) => console is null ? SagaAnalysis.EnqueueStep(db, SagaAnalysis.Iconic) : SagaAnalysis.EnqueueIconic(db, console),
            (kind, data) => kind == SagaAnalysis.Kind && SagaAnalysis.IsStep(data, SagaAnalysis.Iconic)),
        Step("best", SagaAnalysis.BestVersion, "sonnet"),
        Step("translate", SagaAnalysis.Translate, "haiku"),
        new("images", null, [], (db, _) => SteamGridDb.Enqueue(db, missing: true), (kind, _) => kind == "sgdb.images"),
    ];

    static string Setting(string id) => $"task.auto.{id}";

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
        // ponytail: las últimas 1.000 tareas alcanzan para hallar la última de cada una; si no aparece, «nunca corrió».
        var tasks = c.Query<(string Kind, string? Data, string Status, string At)>(
            "SELECT kind, data, status, coalesce(finished_at, created_at) FROM task ORDER BY id DESC LIMIT 1000").ToList();
        return All.Select(t =>
        {
            var last = tasks.FirstOrDefault(x => t.Is(x.Kind, x.Data));
            return new CatalogTaskInfo(t.Id, t.Model, !off.Contains(Setting(t.Id)), t.Id == "igdb" ? days : null, last.Status, last.At,
                (int)t.Prompts.Sum(p => usage.GetValueOrDefault(p).Calls), t.Prompts.Sum(p => usage.GetValueOrDefault(p).Cost));
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

using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <summary>
/// Una tarea de Claude que tiene que esperar (límite de la suscripción, tope por ventana u horario). La cola la
/// devuelve a la espera hasta <see cref="Until"/> con su punto de control, sin contarla como un intento fallido.
/// </summary>
public sealed class ClaudePausedException(DateTime until, ApiError reason) : Exception(reason.Code)
{
    public DateTime Until { get; } = until;
    public ApiError Reason { get; } = reason;
}

/// <summary>
/// Cuándo puede llamar Otteretro a Claude sin quitarte cuota (F2.2, decisiones #154 y #157):
/// en el horario (00:00 a 08:00 por defecto, en la hora del equipo) hasta el límite de la suscripción; fuera de él, solo
/// con «Ejecutar ahora» y sin pasar del tope de cada ventana de 5 h (50 % por defecto, #157). Los ajustes van en la tabla `setting`.
/// </summary>
/// <param name="PausedUntil">Hasta cuándo espera por el límite de la suscripción (null = no espera).</param>
/// <param name="RunNowUntil">Hasta cuándo vale «Ejecutar ahora».</param>
/// <param name="Capped">Si «Ejecutar ahora» para en <see cref="WindowShare"/> (F2.9); si no, usa la ventana entera.</param>
/// <param name="Task">La tarea de Claude pendiente o corriendo, si hay.</param>
public record ClaudeStatus(string ScheduleStart, string ScheduleEnd, int WindowShare, bool Capped, string? PausedUntil, string? RunNowUntil, bool InSchedule, ClaudeTask? Task);

/// <param name="Status">queued o running.</param>
/// <param name="ResumeAt">Si espera, hasta cuándo.</param>
public record ClaudeTask(long Id, string Kind, string Status, long Progress, string? ResumeAt);

public record UsageRow(string Prompt, string? Model, long DurationMs, string Result, long InputTokens, long OutputTokens, double CostUsd, string CreatedAt);

/// <summary>Una página del registro de uso (#95), de la llamada más nueva a la más vieja.</summary>
public record UsagePage(List<UsageRow> Rows, int Page, int Pages);

public record ClaudeSettingsRequest(string? ScheduleStart, string? ScheduleEnd, int? WindowShare, bool? Capped = null);

public static partial class ClaudeBudget
{
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public static readonly TimeSpan Window = TimeSpan.FromHours(5);

    // Ajustes (tabla `setting`, globales). Los valores por defecto valen mientras no se cambian en Ajustes › Claude.
    public const string ScheduleStart = "claude.scheduleStart", ScheduleEnd = "claude.scheduleEnd", WindowShare = "claude.windowShare", Capped = "claude.capped";
    // Estado que lleva la app.
    const string WindowStart = "claude.windowStart", WindowCost = "claude.windowCost", PausedUntil = "claude.pausedUntil", RunNowUntil = "claude.runNowUntil", LastAuto = "claude.lastAuto";

    /// <summary>Antes de cada llamada: lanza <see cref="ClaudePausedException"/> si ahora no se puede.</summary>
    public static void Check(Database db)
    {
        var now = UtcNow();
        using var c = db.Open();
        var s = c.Query<(string Name, string Value)>("SELECT name, value FROM setting WHERE machine_id = '' AND name LIKE 'claude.%'")
            .ToDictionary(r => r.Name, r => r.Value);

        if (Date(s, PausedUntil) is { } paused && paused > now)
            throw new ClaudePausedException(paused, ApiError.Of("tasks.claude.limit", ("at", paused.ToString("O"))));

        var inSchedule = InSchedule(s, now);
        if (!inSchedule && !(Date(s, RunNowUntil) > now))
        {
            var next = NextScheduleStart(s, now);
            throw new ClaudePausedException(next, ApiError.Of("tasks.claude.schedule", ("at", next.ToString("O"))));
        }

        // La ventana empieza con la primera llamada después de que terminó la anterior.
        var start = Date(s, WindowStart);
        if (start is null || start + Window <= now)
        {
            Set(c, WindowStart, now.ToString("O"));
            return;
        }
        // Tope: solo de día, con «Ejecutar ahora» y si está marcado (#157, F2.9); de noche se usa la ventana entera. Y solo
        // cuando ya se sabe cuánto entra en una ventana (se aprende al tocar el límite).
        // ponytail: se mide lo que gasta Otteretro; tus sesiones de Claude Code en la misma ventana no se ven.
        if (!inSchedule && IsCapped(s) && Number(s, WindowCost) is { } capacity && capacity > 0)
        {
            var used = c.ExecuteScalar<double>("SELECT COALESCE(SUM(cost_usd), 0) FROM claude_usage WHERE created_at >= @from",
                new { from = start.Value.ToString("O") });
            var share = Number(s, WindowShare) ?? 50;
            if (used >= capacity * share / 100)
            {
                var end = start.Value + Window;
                throw new ClaudePausedException(end, ApiError.Of("tasks.claude.windowCap", ("share", share.ToString(CultureInfo.InvariantCulture)), ("at", end.ToString("O"))));
            }
        }
    }

    /// <summary>
    /// Si el texto es el aviso de límite de `claude` («You've hit your session limit · resets 8:10pm (America/Santiago)»),
    /// guarda hasta cuándo esperar y lo devuelve. Con el límite de sesión (5 h) además aprende cuánto entra en una ventana.
    /// </summary>
    public static ClaudePausedException? LimitReached(Database db, string text)
    {
        if (!LimitText().IsMatch(text)) return null;
        var now = UtcNow();
        // ponytail: si la hora no se entiende, se espera una hora y se prueba de nuevo (volverá a avisar si sigue).
        var until = ParseReset(text, now) ?? now.AddHours(1);
        using var c = db.Open();
        Set(c, PausedUntil, until.ToString("O"));
        if (SessionLimit().IsMatch(text))
        {
            var start = until - Window;
            var used = c.ExecuteScalar<double>("SELECT COALESCE(SUM(cost_usd), 0) FROM claude_usage WHERE created_at >= @from",
                new { from = start.ToString("O") });
            if (used > 0) Set(c, WindowCost, used.ToString(CultureInfo.InvariantCulture));
            Set(c, WindowStart, start.ToString("O"));
        }
        return new ClaudePausedException(until, ApiError.Of("tasks.claude.limit", ("at", until.ToString("O"))));
    }

    /// <summary>Ajustes › Claude (F2.6): horario, tope de día, si está en pausa y las últimas llamadas.</summary>
    public static ClaudeStatus Status(Database db)
    {
        var now = UtcNow();
        using var c = db.Open();
        var s = c.Query<(string Name, string Value)>("SELECT name, value FROM setting WHERE machine_id = '' AND name LIKE 'claude.%'")
            .ToDictionary(r => r.Name, r => r.Value);
        var (start, end) = Schedule(s);
        var task = c.QueryFirstOrDefault<TaskRead>($"""
            SELECT id, kind, status, progress, resume_at AS ResumeAt FROM task WHERE {Pending} ORDER BY status = 'running' DESC, id LIMIT 1
            """);
        return new ClaudeStatus(start.ToString("HH:mm"), end.ToString("HH:mm"), (int)(Number(s, WindowShare) ?? 50), IsCapped(s),
            Date(s, PausedUntil) is { } p && p > now ? p.ToString("O") : null, Date(s, RunNowUntil) is { } r && r > now ? r.ToString("O") : null,
            InSchedule(s, now), task is null ? null : new ClaudeTask(task.Id, task.Kind, task.Status, task.Progress, task.ResumeAt));
    }

    public const int UsagePageSize = 10;

    /// <summary>La página <paramref name="page"/> (desde 1) del registro de uso; una fuera de rango da la última.</summary>
    public static UsagePage Usage(Database db, int page)
    {
        using var c = db.Open();
        var pages = Math.Max(1, (int)Math.Ceiling(c.ExecuteScalar<long>("SELECT count(*) FROM claude_usage") / (double)UsagePageSize));
        page = Math.Clamp(page, 1, pages);
        // Con propiedades (no el registro): una suma sin filas no tiene tipo y Dapper no encuentra el constructor.
        var rows = c.Query<UsageRead>("""
            SELECT prompt, model, duration_ms AS DurationMs, result, input_tokens + cache_read_tokens + cache_creation_tokens AS InputTokens,
                   output_tokens AS OutputTokens, cost_usd AS CostUsd, created_at AS CreatedAt
            FROM claude_usage ORDER BY id DESC LIMIT @size OFFSET @skip
            """, new { size = UsagePageSize, skip = (page - 1) * UsagePageSize })
            .Select(u => new UsageRow(u.Prompt, u.Model, u.DurationMs, u.Result, u.InputTokens, u.OutputTokens, u.CostUsd, u.CreatedAt)).ToList();
        return new UsagePage(rows, page, pages);
    }

    sealed class TaskRead
    {
        public long Id { get; init; }
        public string Kind { get; init; } = "";
        public string Status { get; init; } = "";
        public long Progress { get; init; }
        public string? ResumeAt { get; init; }
    }

    sealed class UsageRead
    {
        public string Prompt { get; init; } = "";
        public string? Model { get; init; }
        public long DurationMs { get; init; }
        public string Result { get; init; } = "";
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public double CostUsd { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    /// <summary>Guarda los ajustes que vengan; null si están bien, si no, el error.</summary>
    public static ApiError? Save(Database db, ClaudeSettingsRequest request)
    {
        if (request.ScheduleStart is { } a && !TimeOnly.TryParseExact(a, "HH:mm", out _) || request.ScheduleEnd is { } b && !TimeOnly.TryParseExact(b, "HH:mm", out _))
            return ApiError.Of("claude.badTime");
        if (request.WindowShare is < 10 or > 100) return ApiError.Of("claude.badShare");
        using var c = db.Open();
        if (request.ScheduleStart is { } start) Set(c, ScheduleStart, start);
        if (request.ScheduleEnd is { } end) Set(c, ScheduleEnd, end);
        if (request.WindowShare is { } share) Set(c, WindowShare, share.ToString(CultureInfo.InvariantCulture));
        if (request.Capped is { } capped) Set(c, Capped, capped ? "1" : "0");
        return null;
    }

    // Las tareas de Claude que esperan o corren.
    // Una pausada también cuenta (#146): el horario no lanza otra carga encima de la que pausaste.
    const string Pending = "kind LIKE 'claude.%' AND status IN ('queued', 'running', 'paused')";

    /// <summary>
    /// «Ejecutar ahora» (F2.9): salta el horario hasta que empiece el siguiente y lanza ya la carga (o la tarea de Claude que
    /// esperaba). El límite de la suscripción sigue valiendo; el tope, si está marcado. Devuelve el id de la tarea.
    /// </summary>
    public static long RunNow(Database db)
    {
        var now = UtcNow();
        using var c = db.Open();
        var s = Settings(c);
        Set(c, RunNowUntil, NextScheduleStart(s, now).ToString("O"));
        // Lo que esperaba el horario o el tope sale ya; lo que espera el límite, no.
        c.Execute($"UPDATE task SET resume_at = NULL WHERE {Pending} AND status = 'queued' AND detail NOT LIKE '%tasks.claude.limit%'");
        return Launch(c, db);
    }

    /// <summary>
    /// Al empezar el horario, una vez por noche: si no hay nada de Claude en la cola, encola la carga (F2.9). Lo que no cambió
    /// se salta sin llamar a Claude, así que sin cambios termina en segundos. La cola lo llama en cada vuelta.
    /// </summary>
    public static long? AutoStart(Database db)
    {
        var now = UtcNow();
        using var c = db.Open();
        var s = Settings(c);
        if (!InSchedule(s, now)) return null;
        var opened = NextScheduleStart(s, now).AddDays(-1); // cuándo empezó este horario
        if (Date(s, LastAuto) >= opened) return null;
        Set(c, LastAuto, now.ToString("O"));
        Catalog.IgdbImport.RefreshIfDue(db, now); // F2.14: el catálogo al día, cada semana
        return Launch(c, db);
    }

    static long Launch(System.Data.IDbConnection c, Database db) =>
        c.ExecuteScalar<long?>($"SELECT id FROM task WHERE {Pending} ORDER BY id LIMIT 1") ?? InitialLoad.Enqueue(db)!.Value;

    static Dictionary<string, string> Settings(System.Data.IDbConnection c) =>
        c.Query<(string Name, string Value)>("SELECT name, value FROM setting WHERE machine_id = '' AND name LIKE 'claude.%'")
            .ToDictionary(r => r.Name, r => r.Value);

    static bool IsCapped(Dictionary<string, string> s) => !s.TryGetValue(Capped, out var v) || v != "0";

    /// <summary>La hora de «resets …» en UTC: «8:10pm», «3pm» o con fecha, «Nov 3, 9am», en la zona que trae entre paréntesis.</summary>
    public static DateTime? ParseReset(string text, DateTime nowUtc)
    {
        var m = ResetText().Match(text);
        if (!m.Success) return null;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(m.Groups["zone"].Value); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
        var hour = int.Parse(m.Groups["h"].Value) % 12 + (m.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        DateTime at;
        if (m.Groups["date"].Success)
        {
            if (!DateTime.TryParseExact(m.Groups["date"].Value.Trim(), ["MMM d", "MMM d,"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return null;
            at = new DateTime(local.Year, day.Month, day.Day, hour, minute, 0);
            if (at < local.AddDays(-1)) at = at.AddYears(1); // «Jan 2» dicho en diciembre
        }
        else
        {
            at = local.Date.AddHours(hour).AddMinutes(minute);
            if (at <= local) at = at.AddDays(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), zone);
    }

    static bool InSchedule(Dictionary<string, string> s, DateTime nowUtc)
    {
        var (start, end) = Schedule(s);
        var t = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local));
        return start == end || (start < end ? t >= start && t < end : t >= start || t < end); // 22:00–06:00 cruza la medianoche
    }

    static DateTime NextScheduleStart(Dictionary<string, string> s, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        var at = local.Date + Schedule(s).Start.ToTimeSpan();
        if (at <= local) at = at.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), TimeZoneInfo.Local);
    }

    static (TimeOnly Start, TimeOnly End) Schedule(Dictionary<string, string> s) =>
        (Time(s, ScheduleStart) ?? new TimeOnly(0, 0), Time(s, ScheduleEnd) ?? new TimeOnly(8, 0));

    static TimeOnly? Time(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && TimeOnly.TryParseExact(v, "HH:mm", out var t) ? t : null;

    static DateTime? Date(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static double? Number(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && double.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static void Set(System.Data.IDbConnection c, string name, string value) =>
        c.Execute("INSERT INTO setting (name, value, machine_id) VALUES (@name, @value, '') ON CONFLICT (name, machine_id) DO UPDATE SET value = excluded.value",
            new { name, value });

    [GeneratedRegex(@"hit your .*limit|usage limit reached", RegexOptions.IgnoreCase)]
    private static partial Regex LimitText();

    [GeneratedRegex(@"session limit|5-hour limit", RegexOptions.IgnoreCase)]
    private static partial Regex SessionLimit();

    [GeneratedRegex(@"resets\s+(?:(?<date>[A-Z][a-z]{2} \d{1,2},?)\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>am|pm)\s*\((?<zone>[^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetText();
}

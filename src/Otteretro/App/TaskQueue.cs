using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.App;

/// <param name="Status">queued, running, paused, done, error o canceled.</param>
/// <param name="Detail">Qué está haciendo o por qué falló, como código para el front (`tasks.*`, #142).</param>
public record TaskInfo(long Id, string Kind, string Status, int Progress, ApiError? Detail, string CreatedAt, string? FinishedAt);

/// <param name="Level">info, warn o error.</param>
public record TaskLogEntry(long Id, string At, string Level, ApiError Message);

/// <summary>
/// Cola de tareas en la tabla `task` (F1.2): un trabajador en segundo plano con una tarea a la vez por tipo.
/// Revisa la tabla cada pocos segundos, así que también ve lo que encola la CLI desde otro proceso.
/// Cada cambio se publica como evento "task" (panel de tareas).
/// </summary>
public sealed class TaskQueue(AppSession session, Events events, FolderMoveService move, SecretBox box) : BackgroundService
{
    /// <summary>Lo que hace cada tipo de tarea. Recibe el punto de control guardado en `run.Data` y sigue desde ahí.</summary>
    public delegate Task Handler(TaskRun run, CancellationToken ct);

    public static readonly Dictionary<string, Handler> Handlers = new()
    {
        ["demo"] = Demo,
        ["igdb.saga"] = Catalog.IgdbImport.Saga,
        ["igdb.platform"] = Catalog.IgdbImport.Platform,
        ["igdb.refresh"] = Catalog.IgdbImport.Refresh,
        ["igdb.popular"] = Catalog.IgdbImport.Popular,
        ["igdb.popularity"] = Catalog.IgdbImport.Popularity,
        ["sgdb.images"] = Catalog.SteamGridDb.Images,
        [Catalog.GameCodes.Kind] = Catalog.GameCodes.Fill,
        ["sgdb.webp"] = Catalog.SteamGridDb.Convert,
        ["catalog.extra"] = Catalog.ExtraSources.Run,
        [Claude.CatalogCleanup.Kind] = Claude.CatalogCleanup.Run,
        [Claude.SagaAnalysis.Kind] = Claude.SagaAnalysis.Run,
        [Claude.InitialLoad.Kind] = Claude.InitialLoad.Run,
    };

    /// <summary>Tipos que no corren a la vez aunque sean distintos: mover y adoptar tocan las mismas carpetas (§5.3).</summary>
    // Las de Claude van de a una y en orden (#58): depurar antes que rutas, y así con cada paso de la noche.
    static string LockOf(string kind) => kind.StartsWith("library.", StringComparison.Ordinal) ? "library"
        : kind.StartsWith("claude.", StringComparison.Ordinal) ? "claude" : kind;

    /// <summary>Las que duran más que esto avisan con una notificación del sistema al terminar.</summary>
    public static readonly TimeSpan LongTask = TimeSpan.FromMinutes(1);

    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Lanza la carga de Claude al empezar su horario (F2.9). Las pruebas (y el servidor de los e2e, con OTTERETRO_E2E) lo apagan para no depender de la hora.</summary>
    public static bool AutoClaude { get; set; } = Environment.GetEnvironmentVariable("OTTERETRO_E2E") is null;

    /// <summary>
    /// Lo que pasa solo con la app abierta (#219, #223): al abrirla, pasar lo de antes al estándar o escanear, y vigilar `Juegos\`
    /// de cada disco. Las pruebas lo apagan, como <see cref="AutoClaude"/>, y lo prueban por separado.
    /// </summary>
    public static bool OnOpen { get; set; } = Environment.GetEnvironmentVariable("OTTERETRO_E2E") is null;


    /// <summary>
    /// Espera antes de cada reintento de un error pasajero (#59): se corta la red, vence el tiempo, o la fuente responde 429 o 5xx.
    /// La tarea vuelve a la cola con su punto de control; pasados estos intentos sin avanzar, queda en error.
    /// </summary>
    public static TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>En `Exception.Data`: cuánto pidió esperar la fuente (Retry-After).</summary>
    public const string RetryAfterKey = "retryAfter";

    const string Columns = "id, kind, status, progress, detail, data, created_at AS CreatedAt, finished_at AS FinishedAt";

    /// <summary>Lo que corre por grupo: cada tipo de tarea, y las descargas por sitio, que pueden ir varias a la vez (#202).</summary>
    readonly Dictionary<string, int> running = [];
    readonly Lock gate = new();
    /// <summary>Las que corren, con su token y el estado al que pasan si se detienen a pedido (#146).</summary>
    readonly Dictionary<long, (CancellationTokenSource Token, string? StopAs)> live = [];

    /// <summary>Hay alguna tarea corriendo (la mudanza de la carpeta de datos espera a que no haya).</summary>
    public bool Busy { get { lock (gate) return running.Count > 0; } }

    public static long Enqueue(Database db, string kind, string? data = null)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("INSERT INTO task (kind, data, created_at) VALUES (@kind, @data, @now) RETURNING id",
            new { kind, data, now = Now() });
    }

    /// <summary>Anota una línea en el registro de la tarea (F2.10). Sin tarea (la CLI, las pruebas sueltas), no hace nada.</summary>
    public static void Log(Database db, long? taskId, ApiError message, string level = "info")
    {
        if (taskId is null) return;
        using var c = db.Open();
        c.Execute("INSERT INTO task_log (task_id, at, level, message) VALUES (@taskId, @at, @level, @message)",
            new { taskId, at = Now(), level, message = JsonSerializer.Serialize(message, JsonSerializerOptions.Web) });
    }

    /// <summary>El registro de una tarea, lo más nuevo primero (las últimas 500 líneas).</summary>
    public static List<TaskLogEntry> ReadLog(Database db, long taskId)
    {
        using var c = db.Open();
        return c.Query<(long Id, string At, string Level, string Message)>(
                "SELECT id, at, level, message FROM task_log WHERE task_id = @taskId ORDER BY id DESC LIMIT 500", new { taskId })
            .Select(r => new TaskLogEntry(r.Id, r.At, r.Level, JsonSerializer.Deserialize<ApiError>(r.Message, JsonSerializerOptions.Web)!)).ToList();
    }

    /// <summary>
    /// Lo pendiente y lo de las últimas 24 horas (F2.11): primero lo que corre, después la cola y al final lo terminado, por
    /// hora de término (lo más reciente arriba).
    /// </summary>
    public IReadOnlyList<TaskInfo> List()
    {
        if (CurrentDb() is not { } db) return [];
        using var c = db.Open();
        return c.Query<Row>($"""
            SELECT {Columns} FROM task WHERE status IN ('queued', 'running', 'paused') OR finished_at >= @since
            ORDER BY CASE status WHEN 'running' THEN 0 WHEN 'queued' THEN 1 WHEN 'paused' THEN 2 ELSE 3 END, finished_at DESC, id LIMIT 50
            """, new { since = DateTime.UtcNow.AddDays(-1).ToString("O") }).Select(r => r.Info()).ToList();
    }

    /// <summary>«Limpiar terminadas» (F2.11): borra las tareas hechas o con error y su registro. Devuelve cuántas.</summary>
    /// <summary>Borra las tareas terminadas (o solo la `id`, si está terminada; #172) con su registro.</summary>
    public static int Clear(Database db, long? id = null)
    {
        const string finished = "status IN ('done', 'error', 'canceled') AND (@id IS NULL OR id = @id)";
        using var c = db.Open();
        using var t = c.BeginTransaction();
        c.Execute($"DELETE FROM task_log WHERE task_id IN (SELECT id FROM task WHERE {finished})", new { id }, t);
        var count = c.Execute($"DELETE FROM task WHERE {finished}", new { id }, t);
        t.Commit();
        return count;
    }

    /// <summary>
    /// Pausar (#146): una en cola pasa a `paused` al momento; una en curso se detiene en su punto de control y queda `paused`
    /// cuando su paso termina de cortarse. La cola y el arranque no toman las pausadas. False si no está en cola ni corriendo.
    /// </summary>
    public bool Pause(Database db, long id) => Stop(db, id, "paused", ["queued", "running"]);

    /// <summary>Cancelar (#146): en cola, en curso o pausada, termina como `canceled`. Cada tipo sigue sus reglas al cortarse
    /// (mover no borra el origen hasta haber verificado la copia, así que cortado a mitad lo deja como estaba).</summary>
    public bool Cancel(Database db, long id) => Stop(db, id, "canceled", ["queued", "running", "paused"]);

    /// <summary>Reanudar (#146): una pausada vuelve a la cola y sigue desde su punto de control.</summary>
    public bool Resume(Database db, long id)
    {
        int changed;
        using (var c = db.Open())
            changed = c.Execute("UPDATE task SET status = 'queued', resume_at = NULL, detail = NULL WHERE id = @id AND status = 'paused'", new { id });
        if (changed == 0) return false;
        Log(db, id, ApiError.Of("tasks.log.resumed"));
        Publish(db, id);
        return true;
    }

    bool Stop(Database db, long id, string status, string[] from)
    {
        lock (gate)
            if (live.TryGetValue(id, out var run))
            {
                live[id] = run with { StopAs = status };
                run.Token.Cancel();
                return true;
            }
        int changed;
        using (var c = db.Open())
            changed = c.Execute($"UPDATE task SET status = @status, finished_at = {(status == "canceled" ? "@now" : "NULL")} WHERE id = @id AND status IN @from",
                new { id, status, from, now = Now() });
        if (changed == 0) return false;
        Log(db, id, ApiError.Of($"tasks.log.{status}"));
        Publish(db, id);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? recovered = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ponytail: se mira la mudanza antes de tomar una tarea; si las dos empiezan en el mismo instante, la tarea
                // escribe en la carpeta vieja. Un candado común si llega a pasar.
                if (!move.State.Running && CurrentDb() is { } db)
                {
                    if (recovered != db.FilePath)
                    {
                        using (var c = db.Open()) c.Execute("UPDATE task SET status = 'queued' WHERE status = 'running'");
                        recovered = db.FilePath;
                    }
                    if (AutoClaude) Claude.ClaudeBudget.AutoStart(db);
                    StartPending(db, ct);
                }
            }
            catch (Exception e)
            {
                FileLog.Write(LogLevel.Error, "Tasks", e.ToString());
            }
            try { await Task.Delay(Poll, ct); }
            catch (OperationCanceledException) { }
        }
        // Espera a las que están corriendo: al cancelarse quedan en 'running' y siguen en el próximo arranque.
        while (Busy) await Task.Delay(50, CancellationToken.None);
    }

    void StartPending(Database db, CancellationToken ct)
    {
        using var c = db.Open();
        var queued = c.Query<Row>($"""
            SELECT {Columns} FROM task WHERE status = 'queued' AND (resume_at IS NULL OR resume_at <= @now) ORDER BY id
            """, new { now = Now() });
        foreach (var t in queued)
        {
            var (key, limit) = (LockOf(t.Kind), 1);
            lock (gate)
            {
                if (running.GetValueOrDefault(key) >= limit) continue;
                running[key] = running.GetValueOrDefault(key) + 1;
            }
            c.Execute("UPDATE task SET status = 'running', attempts = attempts + 1 WHERE id = @Id", t);
            Log(db, t.Id, ApiError.Of("tasks.log.started"));
            Publish(db, t.Id);
            var token = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (gate) live[t.Id] = (token, null);
            _ = Task.Run(() => Run(db, t, key, token.Token, ct), CancellationToken.None);
        }
    }

    async Task Run(Database db, Row t, string key, CancellationToken ct, CancellationToken app)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (!Handlers.TryGetValue(t.Kind, out var handler)) throw new InvalidOperationException($"Tipo de tarea desconocido: {t.Kind}");
            await handler(new TaskRun(this, db, t.Id, t.Data, new Credentials(db, session.Config.MachineId, box)), ct);
            Finish(db, t.Id, "done", null, clock.Elapsed);
        }
        catch (OperationCanceledException) when (app.IsCancellationRequested) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Stopped(db, t.Id); }
        catch (Claude.ClaudePausedException e) { Pause(db, t, e); }
        catch (Exception e) when (IsTransient(e) && Retry(db, t, e)) { }
        catch (TaskException e)
        {
            FileLog.Write(LogLevel.Warning, "Tasks", $"Tarea {t.Id} ({t.Kind}): {e.Error.Code}");
            Finish(db, t.Id, "error", e.Error, clock.Elapsed);
        }
        catch (Exception e)
        {
            FileLog.Write(LogLevel.Error, "Tasks", $"Tarea {t.Id} ({t.Kind}): {e}");
            Finish(db, t.Id, "error", ApiError.Of("tasks.failed", ("detail", e.Message)), clock.Elapsed);
        }
        finally
        {
            lock (gate)
            {
                if (--running[key] == 0) running.Remove(key);
                if (live.Remove(t.Id, out var run)) run.Token.Dispose();
            }
        }
    }

    /// <summary>Se detuvo a pedido (#146): queda pausada (con su punto de control) o cancelada.</summary>
    void Stopped(Database db, long id)
    {
        string status;
        lock (gate) status = live.GetValueOrDefault(id).StopAs ?? "paused";
        using (var c = db.Open())
            c.Execute($"UPDATE task SET status = @status, attempts = MAX(attempts - 1, 0), finished_at = {(status == "canceled" ? "@now" : "NULL")} WHERE id = @id",
                new { id, status, now = Now() });
        Log(db, id, ApiError.Of($"tasks.log.{status}"));
        Publish(db, id);
    }

    static bool IsTransient(Exception e) => e switch
    {
        HttpRequestException h => h.StatusCode is null or System.Net.HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500,
        TaskCanceledException => true, // venció el tiempo de HttpClient (la cancelación de la app se atrapa antes)
        _ => false,
    };

    /// <summary>Vuelve a poner la tarea en la cola para más tarde. False si ya agotó los intentos.</summary>
    bool Retry(Database db, Row t, Exception e)
    {
        using var c = db.Open();
        var attempt = c.ExecuteScalar<int>("SELECT attempts FROM task WHERE id = @Id", t);
        if (attempt > RetryDelays.Length) return false;
        var wait = RetryDelays[attempt - 1];
        if (e.Data[RetryAfterKey] is TimeSpan asked && asked > wait) wait = asked;
        FileLog.Write(LogLevel.Warning, "Tasks", $"Tarea {t.Id} ({t.Kind}): reintento {attempt} de {RetryDelays.Length} en {wait}: {e.Message}");
        Log(db, t.Id, ApiError.Of("tasks.log.retry", ("detail", e.Message)), "warn");
        c.Execute("UPDATE task SET status = 'queued', resume_at = @at, detail = @detail WHERE id = @Id", new
        {
            t.Id,
            at = DateTime.UtcNow.Add(wait).ToString("O"),
            detail = JsonSerializer.Serialize(ApiError.Of("tasks.retrying", ("attempt", attempt.ToString()), ("total", RetryDelays.Length.ToString()),
                ("minutes", Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)).ToString())), JsonSerializerOptions.Web),
        });
        Publish(db, t.Id);
        return true;
    }

    /// <summary>Claude tiene que esperar (límite, tope u horario, F2.2): vuelve a la cola hasta esa hora sin gastar un intento.</summary>
    void Pause(Database db, Row t, Claude.ClaudePausedException e)
    {
        FileLog.Write(LogLevel.Information, "Tasks", $"Tarea {t.Id} ({t.Kind}): espera hasta {e.Until:O} ({e.Reason.Code})");
        Log(db, t.Id, e.Reason, "warn");
        using (var c = db.Open())
            c.Execute("UPDATE task SET status = 'queued', resume_at = @at, detail = @detail, attempts = MAX(attempts - 1, 0) WHERE id = @Id", new
            {
                t.Id,
                at = e.Until.ToString("O"),
                detail = JsonSerializer.Serialize(e.Reason, JsonSerializerOptions.Web),
            });
        Publish(db, t.Id);
    }

    void Finish(Database db, long id, string status, ApiError? detail, TimeSpan elapsed)
    {
        Log(db, id, detail is not null && status == "error" ? detail : ApiError.Of("tasks.log.done", ("minutes", ((int)Math.Ceiling(elapsed.TotalMinutes)).ToString())),
            status == "error" ? "error" : "info");
        using (var c = db.Open())
            c.Execute("UPDATE task SET status = @status, detail = COALESCE(@detail, detail), progress = CASE WHEN @status = 'done' THEN 100 ELSE progress END, finished_at = @now WHERE id = @id",
                new { id, status, detail = detail is null ? null : JsonSerializer.Serialize(detail, JsonSerializerOptions.Web), now = Now() });
        Publish(db, id);
        if (elapsed >= LongTask) Notify.TaskFinished(session.Config.EffectiveLanguage, status == "error");
    }

    internal void Publish(Database db, long id)
    {
        using var c = db.Open();
        if (c.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM task WHERE id = @id", new { id }) is { } row)
            events.Publish("task", row.Info());
    }

    Database? CurrentDb() =>
        DataFolder.IsReady(session.Config.DataFolder) ? DataFolder.Open(session.Config.DataFolder!) : null;

    static string Now() => DateTime.UtcNow.ToString("O");

    /// <summary>Tarea de prueba (`otteretro tareas prueba`): 10 pasos de 3 segundos; si se corta, sigue desde el último.</summary>
    static async Task Demo(TaskRun run, CancellationToken ct)
    {
        for (var step = int.TryParse(run.Data, out var saved) ? saved : 0; step < 10; step++)
        {
            run.Report(step * 10, ApiError.Of("tasks.demo.step", ("step", (step + 1).ToString()), ("total", "10")), step.ToString());
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    sealed record Row(long Id, string Kind, string Status, long Progress, string? Detail, string? Data, string CreatedAt, string? FinishedAt)
    {
        public TaskInfo Info() => new(Id, Kind, Status, (int)Progress,
            Detail is null ? null : JsonSerializer.Deserialize<ApiError>(Detail, JsonSerializerOptions.Web), CreatedAt, FinishedAt);
    }
}

/// <summary>Un fallo que la persona puede arreglar (falta una clave, no se encontró la saga…): su código va al panel.</summary>
public sealed class TaskException(ApiError error) : Exception(error.Code)
{
    public ApiError Error { get; } = error;
}

/// <summary>Una tarea en curso: informa su avance y guarda su punto de control.</summary>
public sealed class TaskRun(TaskQueue queue, Database db, long id, string? data, Credentials credentials)
{
    public long Id { get; } = id;
    public Database Db { get; } = db;
    public Credentials Credentials { get; } = credentials;

    /// <summary>Anota una línea en el registro de esta tarea (F2.10).</summary>
    public void Log(ApiError message, string level = "info") => TaskQueue.Log(Db, Id, message, level);

    /// <summary>Punto de control con el que arrancó (null la primera vez).</summary>
    public string? Data { get; private set; } = data;

    /// <param name="progress">0 a 100.</param>
    /// <param name="data">Punto de control desde donde seguir si se corta; null deja el anterior.</param>
    public void Report(int progress, ApiError? detail, string? data = null)
    {
        // Avanzar el punto de control reinicia los intentos: una carga de horas aguanta varios cortes separados.
        var advanced = data is not null && data != Data;
        Data = data ?? Data;
        using (var c = Db.Open())
            c.Execute("UPDATE task SET progress = @progress, detail = @detail, data = @Data, attempts = CASE WHEN @advanced THEN 1 ELSE attempts END WHERE id = @Id",
                new { Id, progress = Math.Clamp(progress, 0, 100), detail = detail is null ? null : JsonSerializer.Serialize(detail, JsonSerializerOptions.Web), Data, advanced });
        queue.Publish(Db, Id);
    }
}

/// <summary>Notificación del sistema (libnotify en Linux, aviso de Windows). Si falla, solo queda en el log.</summary>
static class Notify
{
    public static void TaskFinished(string language, bool failed) => Send((language, failed) switch
    {
        ("en", false) => "A background task finished. See it in the tasks panel.",
        ("en", true) => "A background task failed. See it in the tasks panel.",
        (_, false) => "Terminó una tarea en segundo plano. Mírala en el panel de tareas.",
        (_, true) => "Falló una tarea en segundo plano. Mírala en el panel de tareas.",
    });

    static void Send(string text)
    {
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                // Con el acceso directo de Velopack el aviso dice Otteretro; sin él (fuera de la app instalada), «Windows PowerShell».
                start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", """
                    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
                    $x = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
                    $t = $x.GetElementsByTagName('text')
                    $t.Item(0).AppendChild($x.CreateTextNode('Otteretro')) > $null
                    $t.Item(1).AppendChild($x.CreateTextNode($env:OTTERETRO_TEXT)) > $null
                    $app = Get-StartApps -Name Otteretro | Select-Object -First 1
                    $id = if ($app) { $app.AppID } else { '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe' }
                    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($id).Show([Windows.UI.Notifications.ToastNotification]::new($x))
                    """]) { CreateNoWindow = true };
                start.Environment["OTTERETRO_TEXT"] = text;
            }
            else start = new ProcessStartInfo("notify-send", ["Otteretro", text]);
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            FileLog.Write(LogLevel.Warning, "Notify", $"Sin notificación: {e.Message}");
        }
    }
}

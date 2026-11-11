using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Catalog;
using Otteretro.Data;

namespace Otteretro.Tests;

public class TaskQueueTests
{
    static (TaskQueue Queue, Database Db) NewQueue(TempDir tmp)
    {
        var config = new AppConfig { DataFolder = tmp["datos"] };
        Assert.Null(DataFolder.Prepare(config.DataFolder));
        var db = Startup.PrepareData(config);
        var session = new AppSession(config, tmp["config.json"], "k");
        var events = new Events();
        return (new TaskQueue(session, events, new FolderMoveService(session, events), new SecretBox()) { Poll = TimeSpan.FromMilliseconds(20) }, db);
    }

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        if (condition()) return;
        // Solo al fallar: los registros de las demás pruebas (en paralelo) pueden borrarse mientras se leen.
        Assert.Fail(string.Join(" | ", Directory.GetFiles(System.IO.Path.GetTempPath() + "otteretro-pruebas", "*.log", SearchOption.AllDirectories)
            .Select(f => { try { return File.ReadAllText(f); } catch (IOException) { return ""; } })));
    }

    static string Status(Database db, long id)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string>("SELECT status FROM task WHERE id = @id", new { id })!;
    }

    /// <summary>Listo cuando de F1.2: una tarea cortada por un cierre de la app sigue desde su punto de control.</summary>
    [Fact]
    public async Task InterruptedTaskResumesAfterRestart()
    {
        var starts = new List<string?>();
        TaskQueue.Handlers["test-resume"] = async (run, ct) =>
        {
            lock (starts) starts.Add(run.Data);
            for (var step = int.Parse(run.Data ?? "0"); step < 4; step++)
            {
                run.Report(step * 25, null, step.ToString());
                await Task.Delay(step == 2 && run.Data == "2" && starts.Count == 1 ? Timeout.Infinite : 10, ct);
            }
        };
        using var tmp = new TempDir();
        var (first, db) = NewQueue(tmp);
        var id = TaskQueue.Enqueue(db, "test-resume");

        await first.StartAsync(CancellationToken.None);
        await Until(() => starts.Count == 1 && Status(db, id) == "running" && first.List()[0].Progress == 50);
        await first.StopAsync(CancellationToken.None);
        Assert.Equal("running", Status(db, id));

        var (second, _) = NewQueue(tmp);
        await second.StartAsync(CancellationToken.None);
        await Until(() => Status(db, id) == "done");
        await second.StopAsync(CancellationToken.None);

        Assert.Equal([null, "2"], starts);
        Assert.Equal(100, second.List()[0].Progress);
    }

    /// <summary>#146: pausar una en curso la deja quieta con su punto de control, reanudarla sigue desde ahí y cancelar la termina.</summary>
    [Fact]
    public async Task PauseResumeAndCancel()
    {
        var starts = new List<string?>();
        TaskQueue.Handlers["test-pause"] = async (run, ct) =>
        {
            lock (starts) starts.Add(run.Data);
            for (var step = int.Parse(run.Data ?? "0"); step < 4; step++)
            {
                run.Report(step * 25, null, step.ToString());
                await Task.Delay(step == 2 && starts.Count == 1 ? Timeout.Infinite : 10, ct);
            }
        };
        TaskQueue.Handlers["test-forever"] = (run, ct) => Task.Delay(Timeout.Infinite, ct);
        using var tmp = new TempDir();
        var (queue, db) = NewQueue(tmp);
        var id = TaskQueue.Enqueue(db, "test-pause");
        await queue.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => Status(db, id) == "running" && queue.List()[0].Progress == 50);
            Assert.True(queue.Pause(db, id));
            await Until(() => Status(db, id) == "paused");
            await Task.Delay(100); // la cola no la vuelve a tomar
            Assert.Equal("paused", Status(db, id));
            Assert.Single(starts);

            Assert.True(queue.Resume(db, id));
            await Until(() => Status(db, id) == "done");
            Assert.Equal([null, "2"], starts);

            var waiting = TaskQueue.Enqueue(db, "test-forever");
            await Until(() => Status(db, waiting) == "running");
            Assert.True(queue.Cancel(db, waiting));
            await Until(() => Status(db, waiting) == "canceled");
            Assert.False(queue.Resume(db, waiting));
            Assert.Equal(2, TaskQueue.Clear(db)); // las canceladas se limpian como las terminadas
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    /// <summary>#146: una en cola se pausa o cancela sin llegar a correr.</summary>
    [Fact]
    public void QueuedTasksPauseAndCancelWithoutRunning()
    {
        using var tmp = new TempDir();
        var (queue, db) = NewQueue(tmp);
        var a = TaskQueue.Enqueue(db, "demo");
        var b = TaskQueue.Enqueue(db, "demo");
        Assert.True(queue.Pause(db, a));
        Assert.True(queue.Cancel(db, b));
        Assert.Equal("paused", Status(db, a));
        Assert.Equal("canceled", Status(db, b));
        Assert.False(queue.Pause(db, b));
        Assert.True(queue.Cancel(db, a));
        Assert.Equal("canceled", Status(db, a));
    }

    /// <summary>#59: un corte de red no mata la tarea; vuelve a la cola con su punto de control y, sin avanzar, se rinde al tercer reintento.</summary>
    [Fact]
    public async Task TransientErrorsAreRetriedFromTheCheckpoint()
    {
        var starts = new List<string?>();
        TaskQueue.Handlers["test-retry"] = (run, ct) =>
        {
            lock (starts) starts.Add(run.Data);
            if (run.Data is null) run.Report(50, null, "mitad");
            if (starts.Count < 3) throw new HttpRequestException("sin red");
            return Task.CompletedTask;
        };
        TaskQueue.Handlers["test-down"] = (run, ct) => throw new HttpRequestException("caído", null, System.Net.HttpStatusCode.ServiceUnavailable);
        TaskQueue.Handlers["test-bad"] = (run, ct) => throw new HttpRequestException("no existe", null, System.Net.HttpStatusCode.NotFound);
        var delays = TaskQueue.RetryDelays;
        TaskQueue.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            using var tmp = new TempDir();
            var (queue, db) = NewQueue(tmp);
            var (retry, down, bad) = (TaskQueue.Enqueue(db, "test-retry"), TaskQueue.Enqueue(db, "test-down"), TaskQueue.Enqueue(db, "test-bad"));
            await queue.StartAsync(CancellationToken.None);
            await Until(() => new[] { retry, down, bad }.All(id => Status(db, id) is "done" or "error"));
            await queue.StopAsync(CancellationToken.None);

            Assert.Equal("done", Status(db, retry));
            Assert.Equal([null, "mitad", "mitad"], starts); // sigue desde su punto de control
            using var c = db.Open();
            Assert.Equal(("error", 4), c.QuerySingle<(string, int)>("SELECT status, attempts FROM task WHERE id = @down", new { down }));
            Assert.Equal(("error", 1), c.QuerySingle<(string, int)>("SELECT status, attempts FROM task WHERE id = @bad", new { bad })); // un 404 no se reintenta
        }
        finally { TaskQueue.RetryDelays = delays; }
    }

    [Fact]
    public async Task OneTaskAtATimePerKindAndErrorsAreKept()
    {
        int now = 0, max = 0;
        TaskQueue.Handlers["test-serial"] = async (run, ct) =>
        {
            max = Math.Max(max, Interlocked.Increment(ref now));
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref now);
            if (run.Id % 2 == 0) throw new InvalidOperationException("sin red");
        };
        using var tmp = new TempDir();
        var (queue, db) = NewQueue(tmp);
        var ids = Enumerable.Range(0, 3).Select(_ => TaskQueue.Enqueue(db, "test-serial")).ToList();

        await queue.StartAsync(CancellationToken.None);
        await Until(() => ids.All(id => Status(db, id) is "done" or "error"));
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(1, max);
        var failed = queue.List().Single(t => t.Id == ids[1]);
        Assert.Equal("error", failed.Status);
        Assert.Equal(ApiError.Of("tasks.failed", ("detail", "sin red")).Code, failed.Detail!.Code);
        Assert.Equal("sin red", failed.Detail.Params!["detail"]);
    }

    /// <summary>#161: las tareas del catálogo, aunque sean de tipos distintos, corren de a una y en el orden en que se encolaron.</summary>
    [Fact]
    public async Task CatalogTasksRunOneAtATimeInQueueOrder()
    {
        var order = new List<string>();
        int now = 0, max = 0;
        foreach (var kind in new[] { "test-first", "test-second" })
        {
            CatalogTasks.InOrder.Add(kind);
            TaskQueue.Handlers[kind] = async (run, ct) =>
            {
                max = Math.Max(max, Interlocked.Increment(ref now));
                lock (order) order.Add(kind);
                await Task.Delay(kind == "test-first" ? 200 : 10, ct); // la primera tarda más: si corrieran a la vez, la segunda no esperaría
                Interlocked.Decrement(ref now);
            };
        }
        using var tmp = new TempDir();
        var (queue, db) = NewQueue(tmp);
        var ids = new[] { TaskQueue.Enqueue(db, "test-first"), TaskQueue.Enqueue(db, "test-second") };

        await queue.StartAsync(CancellationToken.None);
        await Until(() => ids.All(id => Status(db, id) == "done"));
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(1, max);
        Assert.Equal(["test-first", "test-second"], order);
    }
}

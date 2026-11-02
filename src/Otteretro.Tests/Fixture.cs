using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Tests;

/// <summary>Una carpeta de datos temporal con su cola de tareas y las claves que se le pasen.</summary>
sealed class Fixture : IAsyncDisposable
{
    public readonly TempDir Tmp = new();
    public readonly Database Db;
    public readonly TaskQueue Queue;
    public readonly AppSession Session;
    readonly AppConfig config;
    public AppConfig Config => config;
    readonly SecretBox box = new();

    public Fixture(params (string Id, string Value)[] keys)
    {
        config = new AppConfig { DataFolder = Tmp["datos"] };
        Assert.Null(DataFolder.Prepare(config.DataFolder));
        Db = Startup.PrepareData(config);
        Assert.Null(Catalog.ImageHome.SetStar(Db, Tmp["disco-estrella"])); // el ★ de esta prueba (#9)
        var credentials = new Credentials(Db, config.MachineId, box);
        foreach (var (id, value) in keys) credentials.Save(id, value);
        var session = Session = new AppSession(config, Tmp["config.json"], "k");
        var events = new Events();
        Queue = new TaskQueue(session, events, new FolderMoveService(session, events), box) { Poll = TimeSpan.FromMilliseconds(20) };
    }

    public Credentials Credentials => new(Db, config.MachineId, box);

    /// <summary>Encola, corre la cola hasta que la tarea termina y devuelve cómo terminó.</summary>
    /// <summary>El log puede estar escribiéndose en este momento: otra prueba, en paralelo.</summary>
    static string ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    public async Task<TaskInfo> Run(long id)
    {
        await Queue.StartAsync(CancellationToken.None);
        TaskInfo? task = null;
        for (var i = 0; i < 500; i++)
        {
            task = Queue.List().FirstOrDefault(t => t.Id == id);
            if (task?.Status is "done" or "error") break;
            await Task.Delay(20);
        }
        await Queue.StopAsync(CancellationToken.None);
        var logs = Path.Combine(config.DataFolder!, "logs");
        Assert.True(task is not null, $"La tarea {id} no aparece. Log: " + string.Join(" | ", Directory.GetFiles(logs).Select(ReadShared)));
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.StopAsync(CancellationToken.None);
        Tmp.Dispose();
    }
}

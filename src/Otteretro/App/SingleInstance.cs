using System.IO.Pipes;

namespace Otteretro.App;

/// <summary>
/// Solo una ventana de Otteretro a la vez. Si se abre otra, avisa a la que ya existe
/// (por una tubería con nombre) para que se traiga al frente, y termina.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // El nombre no cambia: así una versión vieja y una nueva siguen viéndose.
    const string Name = "OtteretroCatalogo-instancia";
    readonly Mutex mutex;

    /// <summary>Se dispara (en un hilo de fondo) cuando otra instancia pide traer la ventana al frente.</summary>
    public event Action? Activated;

    SingleInstance(Mutex mutex) => this.mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, @"Local\" + Name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        var instance = new SingleInstance(mutex);
        instance.Listen();
        return instance;
    }

    void Listen() => Task.Run(async () =>
    {
        while (true)
        {
            await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In);
            await pipe.WaitForConnectionAsync();
            Activated?.Invoke();
        }
    });

    public static void NotifyExisting()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(2000);
        }
        catch (TimeoutException) { }
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}

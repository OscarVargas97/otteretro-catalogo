using System.Text.Json;
using System.Threading.Channels;

namespace Otteretro.Api;

/// <summary>Progreso en vivo hacia la interfaz con Server-Sent Events (A2). Cualquier módulo publica aquí.</summary>
public sealed class Events : IDisposable
{
    readonly List<Channel<string>> subscribers = [];
    readonly Lock gate = new();
    readonly Timer heartbeat;

    public Events() =>
        // Mantiene viva la conexión aunque no pase nada.
        heartbeat = new Timer(_ => Publish("heartbeat", new { }), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

    public void Publish(string type, object data)
    {
        var message = $"event: {type}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n";
        lock (gate)
            foreach (var s in subscribers)
                s.Writer.TryWrite(message);
    }

    public async Task Serve(HttpContext http)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (gate) subscribers.Add(channel);
        try
        {
            var aborted = http.RequestAborted;
            await http.Response.WriteAsync(": connected\n\n", aborted);
            await http.Response.Body.FlushAsync(aborted);
            await foreach (var message in channel.Reader.ReadAllAsync(aborted))
            {
                await http.Response.WriteAsync(message, aborted);
                await http.Response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate) subscribers.Remove(channel);
        }
    }

    public void Dispose() => heartbeat.Dispose();
}

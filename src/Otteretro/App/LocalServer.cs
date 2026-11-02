using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Otteretro.Api;

namespace Otteretro.App;

/// <summary>Lo que comparten la ventana y la API durante una sesión.</summary>
/// <param name="Fullscreen">La ventana abrió a pantalla completa (sesión Play): la interfaz ofrece «Ir a Steam».</param>
public sealed record AppSession(AppConfig Config, string ConfigPath, string Key, bool Fullscreen = false);

/// <summary>
/// Servidor local: solo 127.0.0.1, puerto aleatorio y una clave por sesión que solo conoce la ventana.
/// Así ningún otro programa ni página web del PC puede usar la API.
/// </summary>
public static class LocalServer
{
    public const string KeyHeader = "X-Otteretro-Key";

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static WebApplication Create(AppSession session, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLog());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenApi();
        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton<Events>();
        builder.Services.AddSingleton<FolderMoveService>();
        builder.Services.AddSingleton<TaskQueue>();
        builder.Services.AddSingleton<Updater>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskQueue>());
        builder.Services.AddSingleton(new SecretBox());

        // Solo en desarrollo: la interfaz corre en `next dev` en otro origen.
        var devOrigin = Environment.GetEnvironmentVariable("OTTERETRO_DEV_WEB");
        if (devOrigin is not null)
            builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(devOrigin).AllowAnyHeader().AllowAnyMethod()));

        configure?.Invoke(builder);
        var app = builder.Build();

        if (devOrigin is not null) app.UseCors();
        app.Use(async (http, next) =>
        {
            // Un Host distinto delata una página web que intenta llegar a la API (DNS rebinding).
            if (http.Request.Host.Host != "127.0.0.1")
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (http.Request.Path.StartsWithSegments("/api") && !http.Request.Method.Equals("OPTIONS", StringComparison.Ordinal)
                && !IsValidKey(http, session.Key))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });
        app.Use(async (http, next) =>
        {
            if (FindPrefetchFile(app.Environment.WebRootPath, http.Request.Path.Value) is { } file) http.Request.Path = file;
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapOpenApi();
        Endpoints.Map(app);
        return app;
    }

    /// <summary>La clave llega en la cabecera o, para EventSource que no admite cabeceras, en ?key=.</summary>
    static bool IsValidKey(HttpContext http, string key)
    {
        var given = http.Request.Headers[KeyHeader].FirstOrDefault() ?? http.Request.Query["key"].FirstOrDefault();
        return given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// Next 16 pide la precarga de cada pantalla como `sagas/__next.sagas.__PAGE__.txt`, pero la exportación
    /// estática la escribe como `sagas/__next.sagas/__PAGE__.txt`. Si el pedido no existe, se prueba cada forma
    /// de cambiar los puntos finales por carpetas y se devuelve la que existe (o null).
    /// </summary>
    // ponytail: arreglo del lado del servidor; quitarlo cuando Next exporte con los mismos nombres que pide.
    public static string? FindPrefetchFile(string? webRoot, string? requestPath)
    {
        if (webRoot is null || requestPath is null) return null;
        var slash = requestPath.LastIndexOf('/');
        var name = requestPath[(slash + 1)..];
        if (!name.StartsWith("__next.", StringComparison.Ordinal) || !name.EndsWith(".txt", StringComparison.Ordinal)) return null;
        var folder = requestPath[..(slash + 1)];
        if (File.Exists(Path.Join(webRoot, requestPath))) return null;

        var parts = name[..^4].Split('.');
        for (var dots = parts.Length - 1; dots >= 1; dots--)
        {
            var candidate = folder + string.Join('.', parts[..dots]) + "/" + string.Join('/', parts[dots..]) + ".txt";
            if (File.Exists(Path.Join(webRoot, candidate))) return candidate;
        }
        return null;
    }

    /// <summary>La dirección real (con el puerto elegido por el sistema) después de StartAsync.</summary>
    public static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}

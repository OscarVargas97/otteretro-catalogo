using Velopack;

namespace Otteretro.App;

/// <summary>Punto de entrada: el mismo ejecutable es la administración del catálogo y su CLI.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        return args switch
        {
            // En la sesión Play (gamescope) abre a pantalla completa aunque no se pida (#12).
            [] or ["--fullscreen"] => AppMode.Run(args is ["--fullscreen"] || Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY") is not null),
            _ => CliMode.Run(args),
        };
    }
}

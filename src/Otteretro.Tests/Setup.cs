using System.Runtime.CompilerServices;
using Otteretro.App;

namespace Otteretro.Tests;

static class Setup
{
    // Ninguna cola de las pruebas lanza la carga de Claude por su cuenta: depende de la hora y del reloj falso compartido.
    [ModuleInitializer]
    internal static void Init()
    {
        TaskQueue.AutoClaude = false;
        TaskQueue.OnOpen = false; // cada prueba lanza la migración, el escaneo o el vigilante cuando los quiere
        Catalog.GameCodes.Download = false; // las tablas de códigos no se bajan en las pruebas: cada una pone su respuesta (#46)
        // Nunca el ★ real de Otteretro (#9): las pruebas escriben imágenes.
        Catalog.ImageHome.OtteretroConfig = Path.Combine(Path.GetTempPath(), "otteretro-pruebas", "sin-otteretro", "config.json");


    }
}

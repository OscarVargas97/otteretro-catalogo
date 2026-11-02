using System.Reflection;
using Otteretro.Data;

namespace Otteretro.App;

/// <summary>Lo que se hace cada vez que hay una carpeta de datos lista: logs, migraciones y equipo.</summary>
public static class Startup
{
    public static string Version =>
        typeof(Startup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static Database PrepareData(AppConfig config)
    {
        var folder = config.DataFolder ?? throw new InvalidOperationException("No hay carpeta de datos.");
        FileLog.Folder = DataFolder.LogsPath(folder);
        FileLog.Prune();
        DataFolder.Upgrade(folder);
        var db = DataFolder.Open(folder);
        var applied = Migrations.Apply(db);
        Migrations.RegisterMachine(db, config.MachineId);
        Catalog.IgdbImport.EnqueuePopularity(db);
        FolderMove.RememberedSize(folder); // empieza a medirla, para que Ajustes no espere
        FileLog.Write(LogLevel.Information, "Startup", $"Otteretro Catálogo {Version}; carpeta de datos {folder}; {applied} migraciones nuevas.");
        return db;
    }
}

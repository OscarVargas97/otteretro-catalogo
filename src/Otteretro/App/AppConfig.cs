using System.Text.Json;
using System.Text.Json.Serialization;

namespace Otteretro.App;

/// <summary>
/// Lo único que vive fuera de la carpeta de datos (en %LocalAppData%\Otteretro\config.json en Windows
/// y en $XDG_CONFIG_HOME/otteretro/config.json en Linux):
/// dónde está esa carpeta, de dónde llegan las actualizaciones y la identidad de este equipo.
/// Las claves del JSON no cambian: los config.json de F0 se siguen leyendo igual.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("carpetaDatos")]
    public string? DataFolder { get; set; }

    [JsonPropertyName("equipoId")]
    public string MachineId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Idioma elegido en Ajustes; null = el del sistema (#140). Va acá y no en la base porque el asistente corre antes de que exista.</summary>
    public string? Language { get; set; }

    /// <summary>El menú lateral retraído a una franja de íconos (#130). Va acá porque el puerto local cambia en cada arranque y el navegador no lo recordaría.</summary>
    [JsonPropertyName("menuRetraido")]
    public bool MenuCollapsed { get; set; }

    /// <summary>Idiomas con catálogo en web/messages. El primero es el base.</summary>
    public static readonly string[] Languages = ["es", "en"];

    [JsonIgnore]
    public string EffectiveLanguage => Language is { } l && Languages.Contains(l) ? l : SystemLanguage();

    /// <summary>Español si el sistema está en español; si no, inglés.</summary>
    public static string SystemLanguage() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>OTTERETRO_CONFIG permite usar otra configuración (desarrollo y pruebas) sin tocar la real.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("OTTERETRO_CONFIG") ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtteretroCatalogo", "config.json")
        // En Linux, ApplicationData es $XDG_CONFIG_HOME o ~/.config.
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "otteretro-catalogo", "config.json"));

    public static AppConfig Load(string path)
    {
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppConfig>(text, Json) ?? new();
            // Sin equipoId, cada arranque inventaría otro y las claves guardadas de este equipo no se encontrarían (#59).
            if (!JsonDocument.Parse(text).RootElement.TryGetProperty("equipoId", out _)) loaded.Save(path);
            return loaded;
        }
        var config = new AppConfig();
        config.Save(path);
        return config;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Escribir aparte y reemplazar: un corte a mitad nunca deja config.json roto.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

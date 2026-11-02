namespace Otteretro.App;

/// <summary>Registro de errores en archivo: uno por día, se guardan 30 días (A5).</summary>
public sealed class FileLog : ILoggerProvider
{
    /// <summary>Hasta elegir la carpeta de datos, los logs van junto a config.json.</summary>
    public static string Folder { get; set; } = Path.Combine(Path.GetDirectoryName(AppConfig.DefaultPath)!, "logs");

    static readonly Lock Gate = new();

    public ILogger CreateLogger(string category) => new FileLogger(category);
    public void Dispose() { }

    public static void Write(LogLevel level, string category, string message)
    {
        lock (Gate)
        {
            // Escribir el registro nunca puede romper lo que se está registrando.
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"otteretro-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {category}: {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Prune(int days = 30)
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder, "otteretro-*.log"))
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-days))
                File.Delete(file);
    }

    sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Lo de ASP.NET solo a partir de avisos; lo nuestro desde información.
        public bool IsEnabled(LogLevel level) =>
            level >= (category.StartsWith("Microsoft", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            Write(level, category, formatter(state, error) + (error is null ? "" : Environment.NewLine + error));
        }
    }
}

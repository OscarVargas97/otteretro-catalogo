namespace Otteretro.Tests;

/// <summary>Carpeta temporal que se borra al terminar la prueba.</summary>
sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "otteretro-pruebas", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

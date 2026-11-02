using Dapper;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Tests;

public class FolderMoveTests
{
    /// <summary>Una carpeta de datos con base de datos real y un archivo en Herramientas.</summary>
    static string CreateFolder(TempDir tmp)
    {
        var source = tmp["origen"];
        DataFolder.Prepare(source);
        var db = DataFolder.Open(source);
        Migrations.Apply(db);
        Migrations.RegisterMachine(db, "pc-prueba");
        Directory.CreateDirectory(Path.Combine(source, "Herramientas"));
        File.WriteAllBytes(Path.Combine(source, "Herramientas", "emulador.bin"), Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray());
        return source;
    }

    [Fact]
    public void CopiesVerifiesAndKeepsTheDatabase()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        var destination = tmp["destino"];
        var steps = new List<string>();

        FolderMove.CopyAndVerify(source, destination, p => steps.Add(p.Step), forceCopy: true);

        Assert.Contains("copying", steps);
        Assert.Contains("verifying", steps);
        Assert.True(DataFolder.IsReady(destination));
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "Herramientas", "emulador.bin")),
                     File.ReadAllBytes(Path.Combine(destination, "Herramientas", "emulador.bin")));
        using (var c = DataFolder.Open(destination).Open())
            Assert.Equal("pc-prueba", c.ExecuteScalar<string>("SELECT id FROM machine"));
        Assert.True(Directory.Exists(source)); // el origen no se toca hasta DeleteSource

        Assert.Null(FolderMove.DeleteSource(source));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void OnTheSameDriveMovesWithoutCopying()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        var destination = tmp["otra\\carpeta"];

        FolderMove.CopyAndVerify(source, destination, _ => { });

        Assert.False(DataFolder.IsReady(source));
        Assert.True(DataFolder.IsReady(destination));
        Assert.Null(FolderMove.DeleteSource(source)); // quedó vacía: se borra
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public async Task TwoMovesAtOnceStartOnlyOne()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        var destination = tmp["destino"];
        var config = new AppConfig { DataFolder = source };
        using var events = new Otteretro.Api.Events();
        var service = new FolderMoveService(new AppSession(config, tmp["config.json"], "clave"), events);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.Start(destination))));
        for (var i = 0; i < 200 && service.State.Running; i++) await Task.Delay(50);

        Assert.Single(answers, r => r is null);
        Assert.Equal("done", service.State.Step);
        Assert.True(DataFolder.IsReady(destination));
        Assert.Equal(destination, AppConfig.Load(tmp["config.json"]).DataFolder);
    }

    [Fact]
    public void UndoingACopyDeletesOnlyWhatWasCopied()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        var destination = tmp["destino"];
        FolderMove.CopyAndVerify(source, destination, _ => { }, forceCopy: true);
        File.WriteAllText(Path.Combine(destination, "ajeno.txt"), "de otra persona");

        FolderMove.Undo(source, destination);

        Assert.True(DataFolder.IsReady(source));
        Assert.Equal(["ajeno.txt"], Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName));
    }

    [Fact]
    public void UndoingAMoveReturnsTheFolder()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        var destination = tmp["otra\\carpeta"];
        FolderMove.CopyAndVerify(source, destination, _ => { });

        FolderMove.Undo(source, destination);

        Assert.True(DataFolder.IsReady(source));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void AcceptsAnEmptyFolderThatAlreadyExists()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        Directory.CreateDirectory(tmp["vacia"]);
        Assert.Null(FolderMove.Validate(source, tmp["vacia"]));
    }

    [Fact]
    public void RejectsDangerousDestinations()
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        var busy = tmp["ocupada"];
        Directory.CreateDirectory(Path.Combine(busy, "data"));
        File.WriteAllText(Path.Combine(busy, "algo.txt"), "x");

        Assert.NotNull(FolderMove.Validate(source, source));                              // la misma
        Assert.NotNull(FolderMove.Validate(source, source + Path.DirectorySeparatorChar)); // la misma, con barra final
        Assert.NotNull(FolderMove.Validate(source, Path.Combine(source, "dentro")));      // dentro de la actual
        Assert.NotNull(FolderMove.Validate(source, busy));                                // con otros datos
        Assert.NotNull(FolderMove.Validate(source, "relativa"));                          // incompleta
        Assert.NotNull(FolderMove.Validate(source, Path.Combine(busy, "algo.txt")));      // un archivo
    }

    /// <summary>#208: la carpeta de datos es la raíz del disco ★. Moverla lleva solo los datos; los juegos se quedan.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovesOnlyTheDataAndNeverTheGames(bool copy)
    {
        using var tmp = new TempDir();
        var source = CreateFolder(tmp);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(source, @"Juegos\Switch")).FullName, "zelda.nsp"), "juego");
        var other = tmp["otro-disco"];
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(other, @"Juegos\GBA")).FullName, "metroid.gba"), "otro");
        Assert.Null(FolderMove.Validate(source, other));

        FolderMove.CopyAndVerify(source, other, _ => { }, forceCopy: copy);
        Assert.Null(FolderMove.DeleteSource(source));

        Assert.True(DataFolder.IsReady(other));
        Assert.True(File.Exists(Path.Combine(other, @"Herramientas\emulador.bin")));
        Assert.True(File.Exists(Path.Combine(source, @"Juegos\Switch\zelda.nsp")));
        Assert.False(Directory.Exists(Path.Combine(source, "data")));
        Assert.False(Directory.Exists(Path.Combine(other, @"Juegos\Switch")));
        Assert.True(File.Exists(Path.Combine(other, @"Juegos\GBA\metroid.gba")));
        Assert.Equal(0, FolderMove.Size(source));
    }
}

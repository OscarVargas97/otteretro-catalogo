using Otteretro.App;
using Velopack.Sources;

namespace Otteretro.Tests;

public class UpdaterTests
{
    [Fact]
    public async Task OnLinuxItSaysItUpdatesWithTheSystem()
    {
        using var events = new Otteretro.Api.Events();
        var updater = new Updater(new AppSession(new AppConfig { UpdateSource = Path.GetTempPath() }, "config.json", "clave"), events);
        await updater.Check();

        if (OperatingSystem.IsWindows())
            Assert.NotEqual("withSystem", updater.State.Message);
        else
            Assert.Equal((UpdatePhase.Unavailable, "withSystem"), (updater.State.Phase, updater.State.Message));
    }

    /// <summary>#180: cada canal busca en su origen; un config.json de antes con carpeta sigue con ella.</summary>
    [Fact]
    public void EachChannelUsesItsSource()
    {
        Assert.Equal(("win", typeof(GithubSource)), Kind(Updater.SourceFor("stable", Folder)));
        Assert.Equal(("dev", typeof(GithubSource)), Kind(Updater.SourceFor("testing", null)));
        Assert.Equal(("win", typeof(SimpleFileSource)), Kind(Updater.SourceFor("custom", Folder)));
        Assert.Equal(("win", typeof(SimpleWebSource)), Kind(Updater.SourceFor("custom", "https://example.org/otteretro")));
        Assert.Equal(("win", typeof(GithubSource)), Kind(Updater.SourceFor("custom", "https://github.com/alguien/otteretro-catalogo")));
        Assert.Null(Updater.SourceFor("custom", " "));
        Assert.Equal(("win", typeof(SimpleFileSource)), Kind(Updater.SourceFor("local", Folder)));
        Assert.Null(Updater.SourceFor("local", null));

        Assert.Equal("stable", new AppConfig().EffectiveUpdateChannel);
        Assert.Equal("custom", new AppConfig { UpdateSource = Folder }.EffectiveUpdateChannel);
        Assert.Equal("stable", new AppConfig { UpdateSource = Folder, UpdateChannel = "stable" }.EffectiveUpdateChannel);
    }

    /// <summary>El canal Local busca los paquetes en los discos de Otteretro, como Otteretro (#12); el más reciente gana.</summary>
    [Fact]
    public void LocalFindsTheNewestPackagesOnOtteretroDisks()
    {
        using var tmp = new TempDir();
        string Disk(string name, bool marker, DateTime? packages)
        {
            var root = Directory.CreateDirectory(tmp[name]).FullName;
            if (marker) File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Otteretro")).FullName, "otteretro-ubicacion.json"), "{}");
            if (packages is { } at)
            {
                var feed = Path.Combine(Directory.CreateDirectory(Path.Combine(root, Updater.PackagesFolder)).FullName, "releases.win.json");
                File.WriteAllText(feed, "{}");
                File.SetLastWriteTimeUtc(feed, at);
            }
            return root;
        }
        var old = Disk("d", true, DateTime.UtcNow.AddDays(-1));
        var newest = Disk("e", true, DateTime.UtcNow);
        var notOtteretro = Disk("f", false, DateTime.UtcNow.AddDays(1)); // sin marcador no es un disco de Otteretro
        var empty = Disk("g", true, null);

        Assert.Equal(Path.Combine(newest, Updater.PackagesFolder), Updater.NewestPackages([old, newest, notOtteretro, empty, tmp["desconectado"]]));
        Assert.Null(Updater.NewestPackages([empty, notOtteretro]));
    }

    static readonly string Folder = Path.GetTempPath();

    static (string, Type)? Kind((IUpdateSource Source, string Channel)? found) => found is { } f ? (f.Channel, f.Source.GetType()) : null;
}

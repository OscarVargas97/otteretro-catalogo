using Otteretro.App;

namespace Otteretro.Tests;

public class MountsTests
{
    static readonly string[] Linux = ["/", "/boot", "/home", "/mnt/juegos", "/mnt/juegos2", "/nix/store"];

    [Theory]
    [InlineData("/mnt/juegos/OtteretroCatalogo", "/mnt/juegos")]
    [InlineData("/mnt/juegos2/OtteretroCatalogo", "/mnt/juegos2")]
    [InlineData("/mnt/juegos", "/mnt/juegos")]
    [InlineData("/home/player/OtteretroCatalogo", "/home")]
    [InlineData("/opt/OtteretroCatalogo", "/")]
    public void El_montaje_es_el_prefijo_mas_largo(string path, string mount) =>
        Assert.Equal(mount, Mounts.Of(path, Linux));

    [Theory]
    [InlineData("/", true)]
    [InlineData("/home", true)]
    [InlineData("/mnt/juegos", true)]
    [InlineData("/run/media/player/USB", true)]
    [InlineData("/boot", false)]
    [InlineData("/nix/store", false)]
    [InlineData("/homero", false)]
    public void Solo_se_ofrecen_los_discos_de_la_persona(string mount, bool offered) =>
        Assert.Equal(offered, Mounts.IsUserMount(mount));

    [Fact]
    public void InsideSteamTheMemoryRootIsLeftOutAndHomeIsSuggested()
    {
        // Lo que ve Otteretro lanzado por Steam en PlayOS: "/" es un tmpfs y /home el disco real (#39).
        var offer = Mounts.Offer([("/", "tmpfs", 16_000, 16_000), ("/home", "ext4", 800_000, 900_000), ("/mnt/juegos", "ext4", 900_000, 1_000_000)],
            "/home/player", windows: false);

        Assert.DoesNotContain(offer, d => d.Name == "/");
        var home = Assert.Single(offer, d => d.Suggested);
        Assert.Equal(Path.Combine("/home/player", "OtteretroCatalogo"), home.Folder);
        Assert.Equal(Path.Combine("/mnt/juegos", "OtteretroCatalogo"), offer.Single(d => d.Name == "/mnt/juegos").Folder);
    }

    [Fact]
    public void OnWindowsTheDriveWithMoreFreeSpaceIsSuggested()
    {
        var offer = Mounts.Offer([(@"C:\", "NTFS", 100, 500), (@"E:\", "NTFS", 900, 1000)], @"C:\Users\player", windows: true);
        Assert.Equal(@"E:\", Assert.Single(offer, d => d.Suggested).Name);
    }
}

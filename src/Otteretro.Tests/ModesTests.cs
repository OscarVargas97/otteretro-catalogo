using Dapper;
using Otteretro.App;

namespace Otteretro.Tests;

public class ModesTests
{
    [Fact]
    public void VersionReturnsZero()
    {
        var output = new StringWriter();
        Assert.Equal(0, CliMode.Run(["version"], output));
        Assert.Equal(Startup.Version, output.ToString().Trim());
    }

    [Fact]
    public void UnknownCommandReturnsOneAndShowsHelp()
    {
        var output = new StringWriter();
        Assert.Equal(1, CliMode.Run(["volar"], output));
        Assert.Contains("Uso: otteretrocatalogo", output.ToString());
    }

    [Fact]
    public void OpenApiDescribesTheApi()
    {
        using var tmp = new TempDir();
        var file = tmp["openapi.json"];
        Assert.Equal(0, CliMode.Run(["openapi", file], new StringWriter()));
        var json = File.ReadAllText(file);
        Assert.Contains("/api/state", json);
        Assert.Contains("/api/data-folder", json);
        Assert.DoesNotContain("/api/events", json);
    }

    /// <summary>Listo cuando de F1.10: `catalogo cargar` encola la carga (la toma la cola, y se ve en el panel).</summary>
    [Fact]
    public async Task CatalogLoadEnqueuesByConsoleSagaOrAll()
    {
        await using var f = new Fixture();
        using var c = f.Db.Open();
        string Data(long id) => c.ExecuteScalar<string>("SELECT kind || ' ' || data FROM task WHERE id = @id", new { id })!;

        Assert.StartsWith("igdb.platform {\"platform\":\"snes\"", Data(CliMode.EnqueueLoad(f.Db, ["--consola", "SNES"]).Single()));
        Assert.StartsWith("igdb.saga {\"query\":\"the legend of zelda\"", Data(CliMode.EnqueueLoad(f.Db, ["--saga", "the", "legend", "of", "zelda"]).Single()));
        Assert.StartsWith("igdb.popular ", Data(CliMode.EnqueueLoad(f.Db, ["--pc-populares"]).Single()));
        var all = CliMode.EnqueueLoad(f.Db, []);
        Assert.Equal(25, all.Length); // todas menos PC y PC VR, y al final los populares de PC (#62)
        Assert.StartsWith("igdb.popular ", Data(all[^1]));
        Assert.DoesNotContain(all, id => Data(id).Contains("\"pc\""));
    }

    [Fact]
    public void CatalogLoadWithBadOptionsShowsHelp()
    {
        var output = new StringWriter();
        Assert.Equal(1, CliMode.Run(["catalogo", "cargar", "--consola"], output));
        Assert.Contains("catalogo cargar", output.ToString());
    }
}

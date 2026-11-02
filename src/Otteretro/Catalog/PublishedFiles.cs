using System.Text.Json;
using Otteretro.Api;

namespace Otteretro.Catalog;

/// <summary>Lo que necesita publicar con un PR (#22): dónde está cada cosa en lo publicado y qué cambió entre dos versiones de un archivo.</summary>
public static partial class Published
{
    public const string ReplacedPath = ReplacedFile;

    static string Relative(string folder, string file) => Path.GetRelativePath(folder, file).Replace('\\', '/');

    /// <summary>Los archivos de esa saga (normalmente uno; dos si se renombró y quedó el de antes), relativos a la carpeta.</summary>
    public static List<string> SagaFiles(string folder, long id)
    {
        var sagas = Path.Combine(folder, SagasFolder);
        return Directory.Exists(sagas) ? Directory.GetFiles(sagas, $"{id:D5}-*.json").Select(f => Relative(folder, f)).ToList() : [];
    }

    public static IEnumerable<long> GamesIn(string file) => Load<PublishedSaga>(file).Games.Select(g => g.Id);

    /// <summary>En qué archivo está cada juego, relativo a la carpeta.</summary>
    public static Dictionary<long, string> GameFiles(string folder)
    {
        var map = new Dictionary<long, string>();
        var (sagas, loose) = (Path.Combine(folder, SagasFolder), Path.Combine(folder, LooseFolder));
        if (Directory.Exists(sagas))
            foreach (var file in Directory.GetFiles(sagas, "*.json"))
                foreach (var g in Load<PublishedSaga>(file).Games) map[g.Id] = Relative(folder, file);
        if (Directory.Exists(loose))
            foreach (var file in Directory.GetFiles(loose, "*.json"))
                foreach (var g in Load<PublishedLooseGames>(file).Games) map[g.Id] = Relative(folder, file);
        return map;
    }

    /// <summary>Qué cambia en un archivo publicado, para que la persona lo vea antes de abrir el PR. Códigos publish.change.* para el front.</summary>
    public static List<ApiError> Describe(string path, string? before, string? after)
    {
        var changes = new List<ApiError>();
        T? Parse<T>(string? text) where T : class => text is null ? null : JsonSerializer.Deserialize<T>(text, Json);
        if (path == ReplacedFile)
        {
            var old = (Parse<List<PublishedReplacement>>(before) ?? []).ToHashSet();
            var added = (Parse<List<PublishedReplacement>>(after) ?? []).Count(r => !old.Contains(r));
            if (added > 0) changes.Add(ApiError.Of("publish.change.replaced", ("count", added.ToString())));
            return changes;
        }
        List<PublishedGame> gamesBefore, gamesAfter;
        if (path.StartsWith(SagasFolder + "/"))
        {
            var (a, b) = (Parse<PublishedSaga>(before), Parse<PublishedSaga>(after));
            if (a is null) changes.Add(ApiError.Of("publish.change.sagaAdded", ("name", b!.Name)));
            else if (b is null) changes.Add(ApiError.Of("publish.change.sagaRemoved", ("name", a.Name)));
            else
            {
                if (a.Name != b.Name) changes.Add(ApiError.Of("publish.change.name", ("from", a.Name), ("to", b.Name)));
                if (a.Iconic != b.Iconic) changes.Add(ApiError.Of(b.Iconic ? "publish.change.iconicOn" : "publish.change.iconicOff"));
                foreach (var kind in a.Routes.Select(r => r.Kind).Union(b.Routes.Select(r => r.Kind)))
                {
                    var (ra, rb) = (a.Routes.FirstOrDefault(r => r.Kind == kind), b.Routes.FirstOrDefault(r => r.Kind == kind));
                    var code = ra is null ? "routeAdded" : rb is null ? "routeRemoved" : Same(ra with { Id = 0 }, rb with { Id = 0 }) ? null : "routeChanged";
                    if (code is not null) changes.Add(ApiError.Of($"publish.change.{code}", ("kind", kind)));
                }
            }
            (gamesBefore, gamesAfter) = (a?.Games ?? [], b?.Games ?? []);
        }
        else (gamesBefore, gamesAfter) = (Parse<PublishedLooseGames>(before)?.Games ?? [], Parse<PublishedLooseGames>(after)?.Games ?? []);

        var old2 = gamesBefore.ToDictionary(g => g.Id);
        var now = gamesAfter.ToDictionary(g => g.Id);
        foreach (var g in gamesAfter.Where(g => !old2.ContainsKey(g.Id))) changes.Add(ApiError.Of("publish.change.gameAdded", ("name", g.NameEs)));
        foreach (var g in gamesBefore.Where(g => !now.ContainsKey(g.Id))) changes.Add(ApiError.Of("publish.change.gameRemoved", ("name", g.NameEs)));
        foreach (var g in gamesAfter.Where(g => old2.TryGetValue(g.Id, out var o) && !Same(o, g))) changes.Add(ApiError.Of("publish.change.gameChanged", ("name", g.NameEs)));
        return changes;
    }

    static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json);
}

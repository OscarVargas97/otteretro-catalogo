using System.Text;

namespace Otteretro.Catalog;

/// <summary>Un cambio de una saga o un juego entre dos versiones publicadas.</summary>
/// <param name="Change">renamed, merged, removed, added o changed (el juego pasó a otra saga; <paramref name="Into"/> es la saga nueva).</param>
/// <param name="From">El nombre anterior (renamed) o la saga anterior (changed).</param>
/// <param name="Into">Con qué se fusionó (merged) o a qué saga pasó (changed).</param>
public record PublishedChange(string Version, string Kind, string Change, long Id, string? Name = null, string? From = null, long? Into = null);

/// <summary>`cambios.json` (#31, decisiones 260 y 262): todas las versiones y todos los cambios, porque `publicar.yml` conserva solo las 3 últimas releases.</summary>
/// <param name="Images">Cuántos juegos cambiaron de imagen en cada versión (#44): una versión que solo trae imágenes no dice «sin cambios».</param>
public record PublishedHistory(List<string> Versions, List<PublishedChange> Entries, Dictionary<string, int>? Images = null);

public static partial class Published
{
    const string Emulation = "Emulación";

    /// <summary>
    /// Qué cambia de <paramref name="before"/> a <paramref name="after"/>, por id. Lo que falta y figura en `reemplazados.json` está fusionado;
    /// sin entrada, quitado. Un nombre distinto con el mismo id es un renombre.
    /// </summary>
    public static List<PublishedChange> Changes(string before, string after, string version)
    {
        var (b, a) = (Read(before), Read(after));
        var into = a.Replaced.Where(r => r.NewId is not null).ToDictionary(r => (r.Kind, r.OldId), r => r.NewId!.Value);
        var list = new List<PublishedChange>();

        void Compare<T>(string kind, Dictionary<long, T> old, Dictionary<long, T> now, Func<T, string> name)
        {
            foreach (var (id, o) in old.Where(x => !now.ContainsKey(x.Key)))
                list.Add(into.TryGetValue((kind, id), out var to) && now.ContainsKey(to)
                    ? new(version, kind, "merged", id, name(o), Into: to)
                    : new(version, kind, "removed", id, name(o)));
            foreach (var (id, n) in now)
                if (!old.TryGetValue(id, out var o)) list.Add(new(version, kind, "added", id, name(n)));
                else if (name(o) != name(n)) list.Add(new(version, kind, "renamed", id, name(n), From: name(o)));
        }

        Compare("saga", b.Sagas.ToDictionary(s => s.Id), a.Sagas.ToDictionary(s => s.Id), s => s.Name);
        var (oldGames, newGames) = (GamesWithSaga(b), GamesWithSaga(a));
        Compare("game", oldGames, newGames, g => g.Game.NameEs);
        var oldSagas = b.Sagas.ToDictionary(s => s.Id, s => s.Name);
        foreach (var (id, n) in newGames)
            if (oldGames.TryGetValue(id, out var o) && o.SagaId != n.SagaId)
                list.Add(new(version, "game", "changed", id, n.Game.NameEs, From: o.SagaId is { } s ? oldSagas.GetValueOrDefault(s) : Emulation, Into: n.SagaId ?? 0));
        return list;
    }

    /// <summary>Cuántos juegos tienen alguna imagen nueva, cambiada o quitada de <paramref name="before"/> a <paramref name="after"/> (#44).</summary>
    public static int ImageChanges(string before, string after)
    {
        var old = GamesWithSaga(Read(before));
        static string Key(PublishedGame g) => string.Join('|', (g.Images ?? []).OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => $"{i.Key}={i.Value}"));
        return GamesWithSaga(Read(after)).Count(n => Key(n.Value.Game) != (old.TryGetValue(n.Key, out var o) ? Key(o.Game) : ""));
    }

    static Dictionary<long, (PublishedGame Game, long? SagaId)> GamesWithSaga(Everything all) =>
        all.Sagas.SelectMany(s => s.Games.Select(g => (g, (long?)s.Id))).Concat(all.Loose.Select(g => (g, (long?)null))).ToDictionary(x => x.g.Id);

    /// <summary>
    /// El historial con la versión nueva. Sin historial anterior (la primera vez que se publica), arranca desde la versión anterior: así una app que
    /// tenga esa versión ya encuentra la suya.
    /// </summary>
    public static PublishedHistory Extend(PublishedHistory? previous, string? previousVersion, List<PublishedChange> changes, string version, int images = 0)
    {
        var versions = previous?.Versions.ToList() ?? (previousVersion is null ? [] : [previousVersion]);
        if (versions.Contains(version)) return previous!;
        versions.Add(version);
        var counts = new Dictionary<string, int>(previous?.Images ?? []);
        if (images > 0) counts[version] = images;
        return new(versions, [.. previous?.Entries ?? [], .. changes], counts);
    }

    public static PublishedHistory? ReadHistory(string file) => File.Exists(file) ? Load<PublishedHistory>(file) : null;

    public static void SaveHistory(string file, PublishedHistory history) => Save(file, history);

    /// <summary>
    /// Lo que un PR no puede hacer respecto de `main` (#32, decisión 261): perder una saga, un juego o una versión sin entrada en `reemplazados.json`
    /// o reutilizar un id que en `main` es otra cosa. Que el destino de un reemplazo exista ya lo revisa <see cref="Validate"/>.
    /// </summary>
    public static List<string> CheckPullRequest(string main, string head)
    {
        var (m, h) = (Read(main), Read(head));
        var replaced = h.Replaced.Select(r => (r.Kind, r.OldId)).ToHashSet();
        var retired = m.Replaced.Select(r => (r.Kind, r.OldId)).ToHashSet();
        var errors = new List<string>();

        void Check<T>(string kind, string what, Dictionary<long, T> before, Dictionary<long, T> now, Func<T, string> name, Func<T, long?> igdb)
        {
            foreach (var (id, o) in before.Where(x => !now.ContainsKey(x.Key) && !replaced.Contains((kind, x.Key))))
                errors.Add($"{what} {id} («{name(o)}») desaparece sin entrada en reemplazados.json.");
            foreach (var (id, n) in now)
            {
                if (!before.ContainsKey(id) && retired.Contains((kind, id)))
                    errors.Add($"{what} {id} («{name(n)}») usa un id que en main ya se retiró.");
                else if (before.TryGetValue(id, out var o) && igdb(o) is { } a && igdb(n) is { } b && a != b)
                    errors.Add($"{what} {id} («{name(n)}») usa un id que en main es «{name(o)}».");
            }
        }

        Check("saga", "La saga", m.Sagas.ToDictionary(s => s.Id), h.Sagas.ToDictionary(s => s.Id), s => s.Name, s => s.IgdbId);
        Check("game", "El juego", GamesWithSaga(m), GamesWithSaga(h), g => g.Game.NameEs, g => g.Game.IgdbId);
        static Dictionary<long, PublishedVersion> Versions(Everything all) =>
            all.Sagas.SelectMany(s => s.Games).Concat(all.Loose).SelectMany(g => g.Versions).ToDictionary(v => v.Id);
        Check("version", "La versión", Versions(m), Versions(h), v => $"{v.Platform} {v.Kind}", v => null);
        return errors;
    }

    /// <summary>El comentario del PR (#32): cuántos hay de cada cambio y cuáles, con un tope por grupo para que se pueda leer.</summary>
    public static string Summary(List<PublishedChange> changes, List<string> errors)
    {
        const int Shown = 30;
        var text = new StringBuilder("### Cambios del catálogo\n\n");
        if (errors.Count > 0)
        {
            text.Append($"**{errors.Count} problemas que impiden fusionar:**\n\n");
            foreach (var e in errors.Take(Shown)) text.Append($"- {e}\n");
            text.Append('\n');
        }
        var groups = new (string Change, string Title)[] { ("renamed", "Renombrados"), ("merged", "Fusionados"), ("removed", "Quitados"), ("added", "Nuevos"), ("changed", "Cambian de saga") };
        if (changes.Count == 0) text.Append("No cambia ninguna saga ni ningún juego.\n");
        foreach (var (change, title) in groups)
        {
            var items = changes.Where(c => c.Change == change).ToList();
            if (items.Count == 0) continue;
            text.Append($"**{title}: {items.Count}**\n\n");
            foreach (var c in items.Take(Shown))
                text.Append(change switch
                {
                    "renamed" => $"- {Label(c.Kind)} {c.Id}: «{c.From}» → «{c.Name}»\n",
                    "merged" => $"- {Label(c.Kind)} {c.Id} «{c.Name}» → {c.Into}\n",
                    "changed" => $"- {Label(c.Kind)} {c.Id} «{c.Name}»: {c.From} → saga {c.Into}\n",
                    _ => $"- {Label(c.Kind)} {c.Id} «{c.Name}»\n",
                });
            if (items.Count > Shown) text.Append($"- … y {items.Count - Shown} más\n");
            text.Append('\n');
        }
        return text.ToString();
    }

    static string Label(string kind) => kind == "saga" ? "Saga" : "Juego";
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Catalog;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <summary>
/// Depurar el catálogo con Claude (F2.7, #73): las reglas de F1 resuelven lo general; esto es lo que necesita criterio.
/// Un script marca solo las sagas dudosas (repetidos, juegos que no parecen de la saga, sueltos que parecen de ella),
/// Claude las revisa por lotes con Haiku y responde cambios (mover, unir, renombrar) que se aplican solos y quedan en
/// `learning` con lo que había antes, para deshacerlos (decisión de la persona del 2026-10-31). Lo que no cambió no se
/// vuelve a pedir (F2.4). Los cambios sobreviven a recargar IGDB: la saga de un juego se elige una vez, una ficha unida
/// queda como versión, y el nombre de Claude gana al de IGDB (lo manual, siempre).
/// </summary>
public static partial class CatalogCleanup
{
    public const string Kind = "claude.cleanup", PromptName = "depurar-sagas", Source = "claude";

    /// <summary>Juegos por llamada: lo bastante para aprovechar las instrucciones cacheadas, sin respuestas enormes.</summary>
    public static int BatchGames { get; set; } = 300;

    // Lo que ve Claude: una línea por juego (id|nombre|año|consolas).
    public record GameLine(long Id, string Line);
    /// <param name="Rules">Correcciones de la persona para esta saga (F2.12): Claude las cumple siempre.</param>
    public record SagaCase(long Id, string Name, List<string> Reasons, List<GameLine> Games, List<GameLine> Loose, List<string> Rules);

    // Lo que responde.
    /// <param name="Kind">move: los juegos van a otra saga de la entrada (ToSagaId), a una nueva (NewSagaName) o a ninguna;
    /// merge: MergeGameIds son el mismo juego que KeepGameId (pasan a ser sus versiones); rename: la saga SagaId pasa a Name.</param>
    public record Change(string Kind, string Reason, List<long>? GameIds = null, long? ToSagaId = null, string? NewSagaName = null,
        long? KeepGameId = null, List<long>? MergeGameIds = null, long? SagaId = null, string? Name = null);
    /// <param name="Unsure">Sagas que necesitan otra mirada con un modelo mejor (Sonnet).</param>
    public record Reply(List<Change> Changes, List<long> Unsure);

    /// <summary>Punto de control: sagas que faltan, las que pasan a Sonnet y cuántas había.</summary>
    record Job(List<long> Pending, List<long> Unsure, int Total, bool SecondPass = false);

    public static long Enqueue(Database db) => TaskQueue.Enqueue(db, Kind);

    /// <summary>Lo que marca el script, sin gastar tokens (`catalogo depurar --contar`).</summary>
    public record Count(int Sagas, int Games, int Loose, Dictionary<string, int> Reasons, int Calls, long Tokens);

    public static Count Measure(Database db)
    {
        var cases = Cases(db, null);
        var games = cases.Sum(c => c.Games.Count);
        var loose = cases.Sum(c => c.Loose.Count);
        var calls = (int)Math.Ceiling((games + loose) / (double)BatchGames);
        // Con llamadas hechas, lo que gastó de verdad cada una (entrada y salida). Sin ellas, lo medido en la primera depuración
        // real (2026-10-31, 744 sagas en 22 llamadas a Haiku): ~15.500 de entrada y ~19.200 de salida por llamada, ~100 por línea.
        using var c = db.Open();
        var perCall = c.ExecuteScalar<double?>("""
            SELECT avg(input_tokens + cache_read_tokens + cache_creation_tokens + output_tokens) FROM claude_usage
            WHERE prompt = @PromptName AND model = 'haiku' AND result = 'ok'
            """, new { PromptName });
        return new Count(cases.Count, games, loose, cases.SelectMany(x => x.Reasons).GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count()),
            calls, perCall is { } avg ? (long)(calls * avg) : (games + loose) * 100L + calls * 5000L);
    }

    public static Task Run(TaskRun run, CancellationToken ct) =>
        Process(run, run.Data, null, (progress, detail, data) => run.Report(progress, detail, data), ct);

    /// <summary>La depuración, de todas las sagas dudosas o solo de <paramref name="only"/> (una parte de la carga, F2.8).</summary>
    /// <param name="data">Punto de control (null = empezar).</param>
    /// <param name="report">Avance, detalle y punto de control.</param>
    public static async Task Process(TaskRun run, string? data, HashSet<long>? only, Action<int, ApiError, string> report, CancellationToken ct)
    {
        var job = data is null
            ? Start(run.Db, only)
            : JsonSerializer.Deserialize<Job>(data, JsonSerializerOptions.Web)!;
        var prompt = ClaudeRunner.Prompt(PromptName);
        while (true)
        {
            if (job.Pending.Count == 0)
            {
                if (job.SecondPass || job.Unsure.Count == 0) break;
                job = job with { Pending = job.Unsure, Unsure = [], SecondPass = true };
            }
            // El lote se arma con los datos de ahora: lo que cambió un lote anterior entra actualizado.
            var cases = Cases(run.Db, job.Pending.Take(200).ToHashSet())
                .Where(c => ClaudeFingerprint.Changed(run.Db, PromptName, $"saga:{c.Id}", c) || job.SecondPass).ToList();
            var batch = new List<SagaCase>();
            foreach (var c in cases)
            {
                if (batch.Count > 0 && batch.Sum(b => b.Games.Count + b.Loose.Count) + c.Games.Count + c.Loose.Count > BatchGames) break;
                batch.Add(c);
            }
            // Lo que ya no es dudoso o no cambió sale de la lista sin llamar a Claude.
            var skipped = job.Pending.Take(200).Where(id => !cases.Any(c => c.Id == id)).ToHashSet();
            if (batch.Count > 0)
            {
                var reply = await ClaudeRunner.Run<Reply>(run.Db, run.Id, PromptName, prompt, new { sagas = batch.Select(Input) }, ct,
                    job.SecondPass ? "sonnet" : "haiku");
                Apply(run.Db, batch, reply.Value.Changes, run.Id);
                var unsure = job.SecondPass ? [] : reply.Value.Unsure.Where(id => batch.Any(b => b.Id == id)).ToList();
                // La huella es la de cómo quedó la saga: si nada vuelve a cambiar, no se pide otra vez.
                var after = Cases(run.Db, batch.Select(b => b.Id).ToHashSet()).ToDictionary(a => a.Id);
                foreach (var c in batch.Where(c => !unsure.Contains(c.Id)))
                    ClaudeFingerprint.Save(run.Db, PromptName, $"saga:{c.Id}", after.GetValueOrDefault(c.Id) ?? c);
                job = job with { Unsure = [.. job.Unsure, .. unsure] };
            }
            var done = batch.Select(b => b.Id).Concat(skipped).ToHashSet();
            job = job with { Pending = job.Pending.Where(id => !done.Contains(id)).ToList() };
            var reviewed = job.Total - job.Pending.Count;
            report(job.Total == 0 ? 100 : reviewed * 100 / job.Total,
                ApiError.Of("tasks.claude.cleanup", ("done", reviewed.ToString()), ("total", job.Total.ToString())),
                JsonSerializer.Serialize(job, JsonSerializerOptions.Web));
        }
    }

    /// <summary>El orden: primero tus sagas, después las icónicas y las más grandes; las de un solo juego no entran (§F2.9).</summary>
    static Job Start(Database db, HashSet<long>? only)
    {
        var ids = Cases(db, only).Select(c => c.Id).ToList();
        return new Job(ids, [], ids.Count);
    }

    static object Input(SagaCase c) => new
    {
        id = c.Id,
        name = c.Name,
        reasons = c.Reasons,
        games = c.Games.Select(g => g.Line),
        looseCandidates = c.Loose.Select(g => g.Line),
        rules = c.Rules,
    };

    /// <summary>Las sagas dudosas, en orden de prioridad. <paramref name="only"/>: solo esas (para armar un lote).</summary>
    public static List<SagaCase> Cases(Database db, HashSet<long>? only)
    {
        using var c = db.Open();
        var games = c.Query<(long Id, long? SagaId, string Name, long? Year, string? Platforms)>("""
            SELECT g.id, g.saga_id, COALESCE(g.original_name, g.name_es), g.year,
                   (SELECT group_concat(DISTINCT platform) FROM version v WHERE v.game_id = g.id)
            FROM game g
            """).ToList();
        var sagas = c.Query<(long Id, string Name, bool Iconic)>("SELECT id, name, iconic FROM saga").ToDictionary(s => s.Id);
        var rules = c.Query<(long SagaId, string Text)>("SELECT saga_id, text FROM saga_rule ORDER BY id")
            .GroupBy(r => r.SagaId).ToDictionary(g => g.Key, g => g.Select(r => r.Text).ToList());
        var owned = c.Query<long>("""
            SELECT DISTINCT g.saga_id FROM source s JOIN version v ON v.id = s.version_id JOIN game g ON g.id = v.game_id WHERE g.saga_id IS NOT NULL
            """).ToHashSet();

        static string Line((long Id, long? SagaId, string Name, long? Year, string? Platforms) g) =>
            $"{g.Id}|{g.Name}|{g.Year?.ToString() ?? "?"}|{g.Platforms ?? "?"}";

        // Sueltos que parecen de una saga: su nombre empieza con las palabras de la saga («Mario Party Superstars» y la saga
        // Mario Party; «Zombie Panic» no es de «Zombi»). Solo sagas de 2 juegos o más, y si una junta más de 15, su nombre es
        // genérico (Ghost, Pinball) y no se usa.
        var sizes = games.Where(g => g.SagaId is not null).GroupBy(g => g.SagaId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var bySagaWords = sagas.Values.Where(s => sizes.GetValueOrDefault(s.Id) >= 2).Select(s => (s.Id, Words: WordKey(s.Name)))
            .Where(s => s.Words.Replace(" ", "").Length >= 4).GroupBy(s => s.Words).ToDictionary(g => g.Key, g => g.First().Id);
        var loose = new Dictionary<long, List<GameLine>>();
        foreach (var g in games.Where(g => g.SagaId is null))
        {
            var words = WordKey(g.Name).Split(' ');
            for (var n = words.Length - 1; n >= 1; n--) // sin la palabra final: el juego tiene que decir algo más que la saga
                if (bySagaWords.TryGetValue(string.Join(' ', words[..n]), out var sagaId))
                {
                    (loose.TryGetValue(sagaId, out var list) ? list : loose[sagaId] = []).Add(new(g.Id, Line(g)));
                    break;
                }
        }
        foreach (var generic in loose.Where(l => l.Value.Count > MaxLoose).Select(l => l.Key).ToList()) loose.Remove(generic);

        var cases = new List<(SagaCase Case, bool Owned, bool Iconic, int Size)>();
        foreach (var group in games.Where(g => g.SagaId is not null).GroupBy(g => g.SagaId!.Value))
        {
            if (only is not null && !only.Contains(group.Key)) continue;
            if (!sagas.TryGetValue(group.Key, out var saga)) continue;
            var list = group.ToList();
            var looseHere = loose.GetValueOrDefault(group.Key) ?? [];
            var rulesHere = rules.GetValueOrDefault(group.Key) ?? [];
            if (list.Count < 2 && looseHere.Count == 0 && rulesHere.Count == 0) continue; // una saga de un juego no tiene nada que ordenar

            var reasons = new List<string>();
            if (list.GroupBy(g => BaseKey(g.Name)).Any(k => k.Count() > 1)) reasons.Add("similarNames");
            if (Mismatch(saga.Name, list.Select(g => g.Name))) reasons.Add("nameMismatch");
            if (list.Any(g => g.Year is null || g.Platforms is null)) reasons.Add("missingData");
            if (looseHere.Count > 0) reasons.Add("looseCandidates");
            if (rulesHere.Count > 0) reasons.Add("personRules");
            if (reasons.Count == 0) continue;

            cases.Add((new SagaCase(saga.Id, saga.Name, reasons, list.OrderBy(g => g.Year ?? 9999).Select(g => new GameLine(g.Id, Line(g))).ToList(), looseHere, rulesHere),
                owned.Contains(saga.Id), saga.Iconic, list.Count));
        }
        return cases.OrderByDescending(x => x.Owned).ThenByDescending(x => x.Iconic).ThenByDescending(x => x.Size).ThenBy(x => x.Case.Id)
            .Select(x => x.Case).ToList();
    }

    const int MaxLoose = 15;

    /// <summary>El nombre en palabras: minúsculas, solo letras y números, separadas por un espacio.</summary>
    static string WordKey(string name) => string.Join(' ', Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0));

    /// <summary>El nombre sin ediciones ni subtítulos de plataforma, para encontrar posibles repetidos.</summary>
    static string BaseKey(string name) => IgdbImport.NameKey(EditionWords().Replace(name, ""));

    [GeneratedRegex(@"\b(hd|remaster(ed)?|definitive|deluxe|complete|goty|game of the year|edition|collection|anniversary|director'?s cut|3d|ds|switch)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionWords();

    /// <summary>Ningún juego comparte una palabra significativa con el nombre de la saga.</summary>
    static bool Mismatch(string saga, IEnumerable<string> games)
    {
        var words = Words(saga);
        return words.Count > 0 && !games.Any(g => Words(g).Overlaps(words));
    }

    static HashSet<string> Words(string s) =>
        [.. Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 4 && w is not "the" and not "game" and not "games")];

    /// <summary>Aplica lo que respondió Claude, solo sobre lo que había en el lote; cada cambio queda en `learning`.</summary>
    public static void Apply(Database db, List<SagaCase> batch, List<Change> changes, long? taskId = null)
    {
        var sagaIds = batch.Select(b => b.Id).ToHashSet();
        var gameIds = batch.SelectMany(b => b.Games.Concat(b.Loose)).Select(g => g.Id).ToHashSet();
        using var c = db.Open();
        foreach (var change in changes)
        {
            using var t = c.BeginTransaction();
            object? before = change.Kind switch
            {
                "move" when change.GameIds is { Count: > 0 } ids && ids.All(gameIds.Contains)
                            && (change.ToSagaId is null || sagaIds.Contains(change.ToSagaId.Value)) => Move(c, t, ids, change),
                "merge" when change.KeepGameId is { } keep && change.MergeGameIds is { Count: > 0 } merged
                             && merged.Append(keep).All(gameIds.Contains) && !merged.Contains(keep) => Merge(c, t, db, keep, merged),
                "rename" when change.SagaId is { } saga && sagaIds.Contains(saga) && !string.IsNullOrWhiteSpace(change.Name) => Rename(c, t, saga, change.Name.Trim()),
                _ => null,
            };
            if (before is null)
            {
                FileLog.Write(LogLevel.Warning, "Claude", $"Cambio descartado (fuera del lote o incompleto): {JsonSerializer.Serialize(change)}");
                continue;
            }
            var learning = c.ExecuteScalar<long>("INSERT INTO learning (kind, content, created_at, task_id) VALUES ('rule', @content, @now, @taskId) RETURNING id", new
            {
                taskId,
                content = JsonSerializer.Serialize(new { prompt = PromptName, change, before }, Tuples),
                now = DateTime.UtcNow.ToString("O"),
            }, t);
            t.Commit();
            TaskQueue.Log(db, taskId, ApiError.Of($"tasks.log.change.{change.Kind}", ("learning", learning.ToString()), ("reason", change.Reason ?? "")));
        }
    }

    public record MoveBefore(List<(long Game, long? Saga)> Games, long? CreatedSaga);
    public record MergeBefore(long Keep, List<MergedGame> Games);
    public record MergedGame(long Id, long? SagaId, string NameEs, string? OriginalName, long? Year, long? IgdbId, List<long> Versions, bool ProgressMoved,
        List<(string Field, string? Value, string Source, bool Manual)> Fields);
    public record RenameBefore(long Saga, string? Claude);

    static object Move(SqliteConnection c, SqliteTransaction t, List<long> ids, Change change)
    {
        var before = ids.Select(id => (id, c.ExecuteScalar<long?>("SELECT saga_id FROM game WHERE id = @id", new { id }, t))).ToList();
        var target = change.ToSagaId;
        long? created = null;
        if (target is null && !string.IsNullOrWhiteSpace(change.NewSagaName))
            target = created = c.ExecuteScalar<long>("INSERT INTO saga (name) VALUES (@name) RETURNING id", new { name = change.NewSagaName.Trim() }, t);
        c.Execute("UPDATE game SET saga_id = @target WHERE id IN @ids", new { target, ids }, t);
        return new MoveBefore(before, created);
    }

    /// <summary>Las fichas repetidas pasan a ser versiones de la que queda. Una con seguimiento propio no se borra si la que queda también tiene.</summary>
    static object? Merge(SqliteConnection c, SqliteTransaction t, Database db, long keep, List<long> merged)
    {
        var saved = new List<MergedGame>();
        foreach (var id in merged)
        {
            var g = c.QuerySingle<(long? SagaId, string NameEs, string? OriginalName, long? Year, long? IgdbId)>(
                "SELECT saga_id, name_es, original_name, year, igdb_id FROM game WHERE id = @id", new { id }, t);
            var versions = c.Query<long>("SELECT id FROM version WHERE game_id = @id", new { id }, t).ToList();
            var keepHasProgress = c.ExecuteScalar<long>("SELECT count(*) FROM progress WHERE game_id = @keep", new { keep }, t) > 0;
            var hasProgress = c.ExecuteScalar<long>("SELECT count(*) FROM progress WHERE game_id = @id", new { id }, t) > 0;
            if (hasProgress && keepHasProgress) continue; // los dos tienen seguimiento: no se pierde ninguno
            var fields = c.Query<(string, string?, string, bool)>(
                "SELECT field, value, source, manual FROM field_value WHERE entity = 'game' AND entity_id = @id", new { id }, t).ToList();
            c.Execute("UPDATE version SET game_id = @keep WHERE game_id = @id", new { keep, id }, t);
            if (hasProgress) c.Execute("UPDATE progress SET game_id = @keep WHERE game_id = @id", new { keep, id }, t);
            c.Execute("DELETE FROM field_value WHERE entity = 'game' AND entity_id = @id; DELETE FROM game WHERE id = @id", new { id }, t);
            SteamGridDb.Forget(db, id); // sus imágenes (caché) no deben quedar para otro juego que reciba el id
            saved.Add(new MergedGame(id, g.SagaId, g.NameEs, g.OriginalName, g.Year, g.IgdbId, versions, hasProgress, fields));
        }
        return saved.Count == 0 ? null : new MergeBefore(keep, saved);
    }

    static object Rename(SqliteConnection c, SqliteTransaction t, long saga, string name)
    {
        var old = c.ExecuteScalar<string?>("SELECT value FROM field_value WHERE entity = 'saga' AND entity_id = @saga AND field = 'name' AND source = @Source",
            new { saga, Source }, t);
        // Sin fila de IGDB (una saga creada por Claude), la columna es el nombre: se guarda como dato de IGDB para poder volver.
        c.Execute("""
            INSERT INTO field_value (entity, entity_id, field, value, source, manual)
            SELECT 'saga', id, 'name', name, 'igdb', 0 FROM saga s WHERE id = @saga
              AND NOT EXISTS (SELECT 1 FROM field_value WHERE entity = 'saga' AND entity_id = s.id AND field = 'name')
            """, new { saga }, t);
        FieldValues.Set(c, "saga", saga, "name", name, Source, t);
        return new RenameBefore(saga, old);
    }

    /// <summary>Deshace un cambio de Claude (Revisar › Aprendizajes, `catalogo deshacer`). False si no existe o ya se deshizo.</summary>
    public static bool Undo(Database db, long learningId)
    {
        using var c = db.Open();
        using var t = c.BeginTransaction();
        var content = c.ExecuteScalar<string?>("SELECT content FROM learning WHERE id = @learningId AND undone = 0", new { learningId }, t);
        if (content is null) return false;
        // Un alias (#181) solo deja de usarse: lo ya asignado por él se queda.
        if (c.ExecuteScalar<string>("SELECT kind FROM learning WHERE id = @learningId", new { learningId }, t) == "alias")
        {
            c.Execute("UPDATE learning SET undone = 1 WHERE id = @learningId", new { learningId }, t);
            t.Commit();
            return true;
        }
        using var doc = JsonDocument.Parse(content);
        var change = doc.RootElement.GetProperty("change").Deserialize<Change>(JsonSerializerOptions.Web)!;
        var before = doc.RootElement.GetProperty("before");
        switch (change.Kind)
        {
            case "move":
                var move = before.Deserialize<MoveBefore>(Tuples)!;
                foreach (var (game, saga) in move.Games)
                    c.Execute("UPDATE game SET saga_id = @saga WHERE id = @game", new { game, saga }, t);
                if (move.CreatedSaga is { } created && c.ExecuteScalar<long>("SELECT count(*) FROM game WHERE saga_id = @created", new { created }, t) == 0)
                    c.Execute("DELETE FROM saga WHERE id = @created; DELETE FROM saga_user WHERE saga_id = @created", new { created }, t);
                break;
            case "merge":
                var merge = before.Deserialize<MergeBefore>(Tuples)!;
                foreach (var g in merge.Games)
                {
                    // ponytail: si SQLite ya le dio ese id a otro juego, el deshacer falla entero (nada a medias).
                    c.Execute("INSERT INTO game (id, saga_id, name_es, original_name, year, igdb_id) VALUES (@Id, @SagaId, @NameEs, @OriginalName, @Year, @IgdbId)", g, t);
                    foreach (var (field, value, source, manual) in g.Fields)
                        c.Execute("INSERT INTO field_value (entity, entity_id, field, value, source, manual) VALUES ('game', @Id, @field, @value, @source, @manual)",
                            new { g.Id, field, value, source, manual }, t);
                    c.Execute("UPDATE version SET game_id = @Id WHERE id IN @Versions", g, t);
                    if (g.ProgressMoved) c.Execute("UPDATE progress SET game_id = @Id WHERE game_id = @keep", new { g.Id, keep = merge.Keep }, t);
                }
                break;
            case "rename":
                var rename = before.Deserialize<RenameBefore>(Tuples)!;
                if (rename.Claude is { } previous) FieldValues.Set(c, "saga", rename.Saga, "name", previous, Source, t);
                else FieldValues.Clear(c, "saga", rename.Saga, "name", Source, t);
                break;
        }
        c.Execute("UPDATE learning SET undone = 1 WHERE id = @learningId", new { learningId }, t);
        t.Commit();
        return true;
    }

    /// <summary>
    /// «Corregir» (F2.12): deshace el cambio si sigue aplicado y guarda la nota como regla de su saga, que va a Claude en la
    /// próxima depuración (la saga vuelve a ser un caso y su huella cambia). Null si salió bien; si no, el error.
    /// </summary>
    public static ApiError? Correct(Database db, long learningId, string note)
    {
        note = note.Trim();
        if (note.Length is 0 or > 500) return ApiError.Of("claude.badNote");
        long? sagaId;
        using (var c = db.Open())
        {
            var row = c.QuerySingleOrDefault<(string Content, bool Undone)?>("SELECT content, undone FROM learning WHERE id = @learningId", new { learningId });
            if (row is not { } r || !r.Content.Contains($"\"prompt\":\"{PromptName}\"")) return ApiError.Of("claude.noLearning");
            sagaId = SagaOf(c, r.Content);
            if (sagaId is null) return ApiError.Of("claude.noLearning");
            if (!r.Undone && !Undo(db, learningId)) return ApiError.Of("claude.noLearning");
        }
        using (var c = db.Open())
            c.Execute("INSERT INTO saga_rule (saga_id, text, learning_id, created_at) VALUES (@sagaId, @note, @learningId, @now)",
                new { sagaId, note, learningId, now = DateTime.UtcNow.ToString("O") });
        return null;
    }

    /// <summary>La saga de un cambio: la que dejaron los juegos movidos (o a la que llegaron, si estaban sueltos), la del juego que quedó al unir, o la renombrada.</summary>
    internal static long? SagaOf(System.Data.IDbConnection c, string content)
    {
        using var doc = JsonDocument.Parse(content);
        var change = doc.RootElement.GetProperty("change").Deserialize<Change>(JsonSerializerOptions.Web)!;
        var before = doc.RootElement.GetProperty("before");
        return change.Kind switch
        {
            "move" => before.Deserialize<MoveBefore>(Tuples)!.Games.Select(g => g.Saga).FirstOrDefault(s => s is not null)
                      ?? change.ToSagaId ?? before.Deserialize<MoveBefore>(Tuples)!.CreatedSaga,
            "merge" => c.ExecuteScalar<long?>("SELECT saga_id FROM game WHERE id = @KeepGameId", change),
            "rename" => change.SagaId,
            _ => null,
        };
    }

    /// <summary>Las tuplas se guardan como { item1, item2 }.</summary>
    internal static readonly JsonSerializerOptions Tuples = new(JsonSerializerOptions.Web) { IncludeFields = true };
}

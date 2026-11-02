using Dapper;
using Microsoft.Data.Sqlite;
using Otteretro.Api;
using Otteretro.Data;

namespace Otteretro.Catalog;

public record RouteStepEdit(long GameId, long? VersionId, string? Reason);
public record RouteEdit(string? Rationale, List<RouteStepEdit> Steps);

/// <summary>
/// Editar el catálogo a mano (#17): rutas, datos de la saga, sus juegos y sagas nuevas. Cada edición deja una marca en
/// `field_value` con la fuente `manual` (se publica con su saga o su juego), y la carga de IGDB y lo que propone Claude la
/// respetan: lo editado a mano no se pisa.
/// </summary>
public static class Editing
{
    public static readonly string[] RouteKinds = ["release", "story", "community"];

    /// <summary>La marca de una ruta editada (o borrada) a mano: Claude ya no la reemplaza.</summary>
    public static string RouteMark(string kind) => $"route.{kind}";

    /// <summary>Para los UPDATE de `game` que cambian su saga: lo movido a mano no se mueve solo.</summary>
    public const string NotMovedByHand =
        "NOT EXISTS (SELECT 1 FROM field_value m WHERE m.entity = 'game' AND m.entity_id = game.id AND m.field = 'saga_id' AND m.manual = 1)";

    static void Mark(SqliteConnection c, SqliteTransaction t, string entity, long id, string field, string? value) => c.Execute("""
        INSERT INTO field_value (entity, entity_id, field, value, source, manual) VALUES (@entity, @id, @field, @value, 'manual', 1)
        ON CONFLICT (entity, entity_id, field, source) DO UPDATE SET value = excluded.value, manual = 1
        """, new { entity, id, field, value }, t);

    public static bool IsMarked(SqliteConnection c, SqliteTransaction? t, string entity, long id, string field) =>
        c.ExecuteScalar<long>("SELECT count(*) FROM field_value WHERE entity = @entity AND entity_id = @id AND field = @field AND manual = 1",
            new { entity, id, field }, t) > 0;

    static bool Exists(SqliteConnection c, SqliteTransaction? t, string table, long id) =>
        c.ExecuteScalar<long>($"SELECT count(*) FROM {table} WHERE id = @id", new { id }, t) > 0;

    /// <summary>Crea o reemplaza la ruta de ese tipo, con sus pasos en el orden dado. Null si quedó guardada.</summary>
    public static ApiError? SaveRoute(Database db, long sagaId, string kind, RouteEdit edit)
    {
        if (!RouteKinds.Contains(kind)) return ApiError.Of("edit.routeKind");
        if (edit.Steps.Count == 0) return ApiError.Of("edit.emptyRoute");
        using var c = db.Open();
        using var t = c.BeginTransaction();
        if (!Exists(c, t, "saga", sagaId)) return ApiError.Of("edit.noSaga");
        foreach (var step in edit.Steps)
        {
            if (!Exists(c, t, "game", step.GameId)) return ApiError.Of("edit.noGame", ("id", step.GameId.ToString()));
            if (step.VersionId is { } v && c.ExecuteScalar<long>("SELECT count(*) FROM version WHERE id = @v AND game_id = @GameId", new { v, step.GameId }, t) == 0)
                return ApiError.Of("edit.badVersion", ("id", v.ToString()));
        }
        // Se conserva el id: la ruta que alguien sigue (saga_user.chosen_route) sigue apuntando a ella.
        var rationale = string.IsNullOrWhiteSpace(edit.Rationale) ? null : edit.Rationale.Trim();
        var routeId = c.ExecuteScalar<long?>("SELECT id FROM route WHERE saga_id = @sagaId AND kind = @kind", new { sagaId, kind }, t);
        if (routeId is { } existing)
            c.Execute("UPDATE route SET rationale = @rationale, approved = 1 WHERE id = @existing; DELETE FROM route_step WHERE route_id = @existing",
                new { rationale, existing }, t);
        else
            routeId = c.ExecuteScalar<long>("INSERT INTO route (saga_id, kind, rationale, approved) VALUES (@sagaId, @kind, @rationale, 1) RETURNING id",
                new { sagaId, kind, rationale }, t);
        var position = 0;
        foreach (var step in edit.Steps)
            c.Execute("INSERT INTO route_step (route_id, position, game_id, recommended_version_id, reason) VALUES (@routeId, @position, @GameId, @VersionId, @reason)",
                new { routeId, position = ++position, step.GameId, step.VersionId, reason = string.IsNullOrWhiteSpace(step.Reason) ? null : step.Reason.Trim() }, t);
        Mark(c, t, "saga", sagaId, RouteMark(kind), "edited");
        t.Commit();
        return null;
    }

    /// <summary>Borra la ruta de ese tipo; queda marcada para que Claude no la vuelva a proponer sola.</summary>
    public static ApiError? DeleteRoute(Database db, long sagaId, string kind)
    {
        if (!RouteKinds.Contains(kind)) return ApiError.Of("edit.routeKind");
        using var c = db.Open();
        using var t = c.BeginTransaction();
        if (!Exists(c, t, "saga", sagaId)) return ApiError.Of("edit.noSaga");
        c.Execute("DELETE FROM route WHERE saga_id = @sagaId AND kind = @kind", new { sagaId, kind }, t);
        Mark(c, t, "saga", sagaId, RouteMark(kind), "deleted");
        t.Commit();
        return null;
    }

    public static ApiError? RenameSaga(Database db, long sagaId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ApiError.Of("fields.empty");
        using var c = db.Open();
        if (!Exists(c, null, "saga", sagaId)) return ApiError.Of("edit.noSaga");
        FieldValues.Set(c, "saga", sagaId, "name", name.Trim(), FieldValues.Manual);
        return null;
    }

    /// <summary>Si la saga es icónica. Claude ya no lo cambia (ni sus juegos icónicos).</summary>
    public static ApiError? SetIconic(Database db, long sagaId, bool iconic)
    {
        using var c = db.Open();
        using var t = c.BeginTransaction();
        if (!Exists(c, t, "saga", sagaId)) return ApiError.Of("edit.noSaga");
        c.Execute("UPDATE saga SET iconic = @iconic WHERE id = @sagaId", new { iconic, sagaId }, t);
        Mark(c, t, "saga", sagaId, "iconic", iconic ? "1" : "0");
        t.Commit();
        return null;
    }

    /// <summary>
    /// Una saga nueva, con el siguiente id libre. Los ids de saga no se reutilizan: una saga borrada nunca tuvo un id mayor que el
    /// máximo vigente al crearla. Choques entre dos PR abiertos a la vez los rechaza la validación del PR (#17).
    /// </summary>
    public static (long? Id, ApiError? Error) CreateSaga(Database db, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, ApiError.Of("fields.empty"));
        using var c = db.Open();
        using var t = c.BeginTransaction();
        var id = c.ExecuteScalar<long>("SELECT coalesce(max(id), 0) + 1 FROM saga", transaction: t);
        c.Execute("INSERT INTO saga (id, name) VALUES (@id, @name)", new { id, name = name.Trim() }, t);
        FieldValues.Set(c, "saga", id, "name", name.Trim(), FieldValues.Manual, t);
        t.Commit();
        return (id, null);
    }

    /// <summary>Pasa un juego a otra saga, o a ninguna (null). Ni IGDB ni Claude lo vuelven a mover.</summary>
    public static ApiError? MoveGame(Database db, long gameId, long? sagaId)
    {
        using var c = db.Open();
        using var t = c.BeginTransaction();
        if (!Exists(c, t, "game", gameId)) return ApiError.Of("edit.noGame", ("id", gameId.ToString()));
        if (sagaId is { } s && !Exists(c, t, "saga", s)) return ApiError.Of("edit.noSaga");
        c.Execute("UPDATE game SET saga_id = @sagaId WHERE id = @gameId", new { sagaId, gameId }, t);
        Mark(c, t, "game", gameId, "saga_id", sagaId?.ToString());
        t.Commit();
        return null;
    }

    /// <summary>
    /// Fusiona un juego en otro: sus versiones y sus pasos de ruta pasan al que queda, y queda en `reemplazados.json` para que
    /// Otteretro lleve lo de cada persona al juego que lo reemplaza.
    /// </summary>
    public static ApiError? MergeGame(Database db, long gameId, long intoId)
    {
        if (gameId == intoId) return ApiError.Of("edit.sameGame");
        using (var c = db.Open())
        using (var t = c.BeginTransaction())
        {
            if (!Exists(c, t, "game", gameId)) return ApiError.Of("edit.noGame", ("id", gameId.ToString()));
            if (!Exists(c, t, "game", intoId)) return ApiError.Of("edit.noGame", ("id", intoId.ToString()));
            c.Execute("""
                UPDATE version SET game_id = @intoId WHERE game_id = @gameId;
                UPDATE route_step SET game_id = @intoId WHERE game_id = @gameId;
                UPDATE progress SET game_id = @intoId WHERE game_id = @gameId AND NOT EXISTS (SELECT 1 FROM progress WHERE game_id = @intoId);
                """, new { gameId, intoId }, t);
            Remove(c, t, gameId, intoId);
            t.Commit();
        }
        SteamGridDb.Forget(db, gameId);
        return null;
    }

    /// <summary>Quita un juego del catálogo, con sus versiones; los dos quedan en `reemplazados.json` sin reemplazo.</summary>
    public static ApiError? DeleteGame(Database db, long gameId)
    {
        using (var c = db.Open())
        using (var t = c.BeginTransaction())
        {
            if (!Exists(c, t, "game", gameId)) return ApiError.Of("edit.noGame", ("id", gameId.ToString()));
            foreach (var version in c.Query<long>("SELECT id FROM version WHERE game_id = @gameId", new { gameId }, t))
                c.Execute("""
                    UPDATE replaced SET new_id = NULL WHERE kind = 'version' AND new_id = @version;
                    INSERT OR REPLACE INTO replaced (kind, old_id, new_id) VALUES ('version', @version, NULL);
                    """, new { version }, t);
            Remove(c, t, gameId, null);
            t.Commit();
        }
        SteamGridDb.Forget(db, gameId);
        return null;
    }

    /// <summary>Borra el juego y anota con qué se reemplaza; lo que ya apuntaba a él pasa a apuntar al reemplazo.</summary>
    static void Remove(SqliteConnection c, SqliteTransaction t, long gameId, long? newId) => c.Execute("""
        DELETE FROM field_value WHERE entity = 'game' AND entity_id = @gameId;
        DELETE FROM game WHERE id = @gameId;
        UPDATE replaced SET new_id = @newId WHERE kind = 'game' AND new_id = @gameId;
        INSERT OR REPLACE INTO replaced (kind, old_id, new_id) VALUES ('game', @gameId, @newId);
        """, new { gameId, newId }, t);
}

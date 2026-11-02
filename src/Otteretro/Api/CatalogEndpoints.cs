using Dapper;
using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Otteretro.App;
using Otteretro.Catalog;
using Otteretro.Data;

namespace Otteretro.Api;

public record SagaFlagsRequest(bool? Favorite, bool? Following);
public record FieldRequest(string? Value);
public record IconicRequest(bool Iconic);
public record NewSagaRequest(string? Name);
public record NewSagaResponse(long Id);
/// <param name="SagaId">Null: sin saga («Emulación»).</param>
public record MoveGameRequest(long? SagaId);
public record MergeGameRequest(long IntoId);

/// <summary>Una consola o un juego (#179).</summary>
public record VerifyRequest(string? Console, long? GameId);

/// <summary>El ejecutable de un juego de PC suelto, relativo a su carpeta (#182).</summary>
public record ExecutableRequest(string Path);
/// <param name="Skip">Lo que se desmarcó en la vista previa, relativo a la carpeta.</param>
public record DownloadRequest(string Name, string? DiskId, List<string> Links, long? GameId = null);
public record CaptureRequest(string Text, long? GameId = null);
public record QueueLinksRequest(List<string> Packages, string? DiskId);
public record ClipboardRequest(bool Enabled);
public record SiteLimitRequest(string Site, int Value);
public record ConnectionsRequest(int Connections);
public record ImportRequest(string Path, List<string>? Skip = null);
public record PreviewRequest(string Path);
public record RegionsRequest(List<string> Order);
public record ConvertRequest(string Console);
/// <param name="Ids">Las filas de <c>source</c> que se quitan del disco (#224).</param>
public record TrashRequest(List<long> Ids);

/// <summary>Pantallas del catálogo (F1.9): sagas, colección, ficha, seguimiento, búsqueda y avisos de Inicio.</summary>
public static class CatalogEndpoints
{
    /// <summary>Claves sin las que una parte del catálogo no funciona: van como aviso en Inicio (§2, #44).</summary>
    static readonly (string Service, string[] Ids)[] NeededKeys =
    [
        ("igdb", ["igdb.clientId", "igdb.clientSecret"]),
        ("steamgriddb", ["steamgriddb.apiKey"]),
    ];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/sagas", Results<Ok<SagaPage>, Conflict<ApiError>> (AppSession s, string? filter, string? platform, string? company, string? sort,
            int offset = 0, int limit = CatalogQueries.PageSize) =>
            Db(s) is { } db ? TypedResults.Ok(CatalogQueries.SagasPage(db, filter, platform, company, sort, Math.Max(0, offset), Math.Clamp(limit, 1, 500))) : NoData());

        api.MapGet("/sagas/{id:long}", Results<Ok<SagaDetail>, NotFound, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is not { } db ? NoData() : CatalogQueries.Saga(db, id) is { } saga ? TypedResults.Ok(saga) : TypedResults.NotFound());

        api.MapGet("/sagas/{id:long}/games", Results<Ok<GamePage>, Conflict<ApiError>> (long id, AppSession s, int offset = 0, int limit = CatalogQueries.PageSize) =>
            Db(s) is { } db ? TypedResults.Ok(CatalogQueries.SagaGames(db, id, Math.Max(0, offset), Math.Clamp(limit, 1, 500))) : NoData());

        api.MapGet("/consoles", Results<Ok<List<ConsoleCount>>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(CatalogQueries.Consoles(db)) : NoData());

        // Ajustes › Consolas (F2.13, F2.14): habilitar consolas desde IGDB y el catálogo al día.
        api.MapGet("/consoles/settings", Results<Ok<ConsolesPage>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(Consoles.Page(db)) : NoData());
        api.MapGet("/consoles/igdb", async Task<Results<Ok<List<PlatformInfo>>, Conflict<ApiError>>> (AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            try { return TypedResults.Ok(await Consoles.Available(db, IgdbFor(db, s, box), ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
        });
        api.MapPost("/consoles", async Task<Results<Ok<ConsolesPage>, NotFound, Conflict<ApiError>>> (EnableConsoleRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            try { return await Consoles.Enable(db, IgdbFor(db, s, box), request.IgdbId, ct) is null ? TypedResults.NotFound() : TypedResults.Ok(Consoles.Page(db)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
        });
        api.MapPut("/consoles/{id}", Results<Ok<ConsolesPage>, NotFound, Conflict<ApiError>> (string id, ConsoleUpdateRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Consoles.SetEnabled(db, id, request.Enabled) ? TypedResults.Ok(Consoles.Page(db)) : TypedResults.NotFound());
        api.MapPut("/catalog/refresh", Results<Ok<ConsolesPage>, BadRequest<ApiError>, Conflict<ApiError>> (RefreshSettingsRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Consoles.SaveRefreshDays(db, request.Days) is { } error ? TypedResults.BadRequest(error) : TypedResults.Ok(Consoles.Page(db)));
        api.MapPost("/catalog/refresh", Results<Ok<long>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(IgdbImport.EnqueueRefresh(db)) : NoData());

        // Portadas a pedido (#132).
        api.MapGet("/covers", Results<Ok<SteamGridDb.CoversInfo>, Conflict<ApiError>> (AppSession s, SecretBox box) =>
            Db(s) is { } db ? TypedResults.Ok(SteamGridDb.Status(db, HasSgdbKey(db, s, box))) : NoData());
        api.MapPost("/covers", Results<Ok<SteamGridDb.CoversInfo>, Conflict<ApiError>> (AppSession s, SecretBox box, bool missing = false) =>
            Db(s) is { } db ? TypedResults.Ok(SteamGridDb.Request(db, HasSgdbKey(db, s, box), missing)) : NoData());

        api.MapGet("/games/{id:long}", Results<Ok<GameDetail>, NotFound, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is not { } db ? NoData() : CatalogQueries.Game(db, id) is { } game ? TypedResults.Ok(game) : TypedResults.NotFound());

        // Corregir a mano un dato (§6.8): gana siempre y ninguna carga lo pisa. DELETE vuelve al de las fuentes.
        api.MapPut("/games/{id:long}/fields/{field}", Results<Ok<GameDetail>, BadRequest<ApiError>, NotFound, Conflict<ApiError>> (long id, string field, FieldRequest request, AppSession s) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (!CatalogQueries.EditableFields.Contains(field)) return TypedResults.NotFound();
            var value = request.Value?.Trim() ?? "";
            if (value.Length == 0 && field == "name_es") return TypedResults.BadRequest(ApiError.Of("fields.empty"));
            if (field == "year" && !(int.TryParse(value, out var year) && year is >= 1970 and <= 2100)) return TypedResults.BadRequest(ApiError.Of("fields.badYear"));
            if (CatalogQueries.Game(db, id) is null) return TypedResults.NotFound();
            using (var c = db.Open()) FieldValues.Set(c, "game", id, field, value.Length == 0 ? null : value, FieldValues.Manual);
            return TypedResults.Ok(CatalogQueries.Game(db, id)!);
        });

        api.MapDelete("/games/{id:long}/fields/{field}", Results<Ok<GameDetail>, NotFound, Conflict<ApiError>> (long id, string field, AppSession s) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (!CatalogQueries.EditableFields.Contains(field) || CatalogQueries.Game(db, id) is null) return TypedResults.NotFound();
            using (var c = db.Open()) FieldValues.Clear(c, "game", id, field, FieldValues.Manual);
            return TypedResults.Ok(CatalogQueries.Game(db, id)!);
        });

        // Editar a mano (#17): lo que se cambia acá ni IGDB ni Claude lo pisan, y se publica con su saga.
        api.MapPut("/sagas/{id:long}/routes/{kind}", Results<Ok<Claude.RoutesPage>, BadRequest<ApiError>, Conflict<ApiError>> (long id, string kind, RouteEdit request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.SaveRoute(db, id, kind, request) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok(Claude.ClaudeReview.Routes(db, id)));
        api.MapDelete("/sagas/{id:long}/routes/{kind}", Results<Ok<Claude.RoutesPage>, BadRequest<ApiError>, Conflict<ApiError>> (long id, string kind, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.DeleteRoute(db, id, kind) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok(Claude.ClaudeReview.Routes(db, id)));
        api.MapPut("/sagas/{id:long}/name", Results<Ok<SagaDetail>, BadRequest<ApiError>, Conflict<ApiError>> (long id, FieldRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.RenameSaga(db, id, request.Value) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok(CatalogQueries.Saga(db, id)!));
        api.MapPut("/sagas/{id:long}/iconic", Results<Ok<SagaDetail>, BadRequest<ApiError>, Conflict<ApiError>> (long id, IconicRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.SetIconic(db, id, request.Iconic) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok(CatalogQueries.Saga(db, id)!));
        api.MapPost("/sagas", Results<Ok<NewSagaResponse>, BadRequest<ApiError>, Conflict<ApiError>> (NewSagaRequest request, AppSession s) =>
        {
            if (Db(s) is not { } db) return NoData();
            var (id, error) = Editing.CreateSaga(db, request.Name);
            return error is not null ? TypedResults.BadRequest(error) : TypedResults.Ok(new NewSagaResponse(id!.Value));
        });
        api.MapPut("/games/{id:long}/saga", Results<Ok, BadRequest<ApiError>, Conflict<ApiError>> (long id, MoveGameRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.MoveGame(db, id, request.SagaId) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok());
        api.MapPost("/games/{id:long}/merge", Results<Ok, BadRequest<ApiError>, Conflict<ApiError>> (long id, MergeGameRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.MergeGame(db, id, request.IntoId) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok());
        api.MapDelete("/games/{id:long}", Results<Ok, BadRequest<ApiError>, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is not { } db ? NoData() : Editing.DeleteGame(db, id) is { } e ? TypedResults.BadRequest(e) : TypedResults.Ok());

        // Publicar tu versión como un PR (#22). Solo propone: nada acá fusiona.
        api.MapPost("/publish/preview", async Task<Results<Ok<PublishPreview>, Conflict<ApiError>>> (PublishRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await publisher.Preview(request, ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });
        api.MapPost("/publish", async Task<Results<Ok<PublishResult>, Conflict<ApiError>>> (PublishRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await publisher.Publish(request, ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });

        api.MapGet("/search", Results<Ok<SearchResults>, Conflict<ApiError>> (AppSession s, string? q) =>
        {
            if (Db(s) is not { } db) return NoData();
            var text = q?.Trim() ?? "";
            return TypedResults.Ok(text.Length < 2 ? new SearchResults([], []) : CatalogQueries.Search(db, text));
        });

        // Avisos de Inicio: por ahora, las claves que faltan (el código lo traduce el front: notices.*).
        api.MapGet("/notices", Results<Ok<List<ApiError>>, Conflict<ApiError>> (AppSession s, SecretBox box) =>
        {
            if (Db(s) is not { } db) return NoData();
            var saved = new Credentials(db, s.Config.MachineId, box).List().Where(c => c.Saved).Select(c => c.Id).ToHashSet();
            var notices = NeededKeys.Where(k => !k.Ids.All(saved.Contains)).Select(k => ApiError.Of("notices.missingKey", ("service", k.Service))).ToList();
            // Archivos que ni el escaneo ni Claude reconocieron (#180): se eligen en Revisar › Sin identificar.
            using (var c = db.Open())
                if (c.ExecuteScalar<long>("SELECT count(*) FROM review_item WHERE kind = 'unidentified' AND status = 'open'") is > 0 and var n)
                    notices.Add(ApiError.Of("notices.unidentified", ("count", n.ToString())));
            return TypedResults.Ok(notices);
        });
    }

    static Igdb IgdbFor(Database db, AppSession s, SecretBox box) =>
        new(new ApiClient(db, IgdbImport.Handler), new Credentials(db, s.Config.MachineId, box), IgdbImport.Handler);

    static GitHubPublisher? Publisher(Database db, AppSession s, SecretBox box) =>
        new Credentials(db, s.Config.MachineId, box).Read("github.token") is { Length: > 0 } token ? new(db, token) : null;

    static bool HasSgdbKey(Database db, AppSession s, SecretBox box) =>
        new Credentials(db, s.Config.MachineId, box).List().Any(c => c.Id == "steamgriddb.apiKey" && c.Saved);

    internal static Database? Db(AppSession s) =>
        DataFolder.IsReady(s.Config.DataFolder) ? DataFolder.Open(s.Config.DataFolder!) : null;

    internal static Conflict<ApiError> NoData() => TypedResults.Conflict(ApiError.Of("move.noDataFolder"));

    static bool IsDate(string? value) =>
        value is null || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

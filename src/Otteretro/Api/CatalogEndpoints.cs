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
/// <summary>La versión publicada a bajar (#105).</summary>
public record CatalogApplyRequest(string Tag);
/// <param name="UseRemote">True: lo de la versión; false: lo local se queda.</param>
public record ConflictChoice(bool UseRemote);

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

        // «Completar códigos» (#46): el TitleID o el serial de cada versión, para que Otteretro reconozca por ID.
        api.MapPost("/codes", Results<Ok<long>, Conflict<ApiError>> (AppSession s) => Db(s) is { } db ? TypedResults.Ok(GameCodes.Enqueue(db)) : NoData());

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

        // «Emuladores» (#95): las recetas en la base local, como las sagas; Publicar lleva al PR las que cambiaron.
        api.MapGet("/recipes", Results<Ok<List<RecipeInfo>>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(Recipes.List(db)) : NoData());
        // El editor de recetas (#111) se arma con el esquema: el mismo que valida al guardar.
        api.MapGet("/recipes/schema", () => TypedResults.Ok(new RecipeText(Published.RecipeSchema())));
        api.MapGet("/recipes/{id}", Results<Ok<RecipeText>, NotFound, Conflict<ApiError>> (string id, AppSession s) =>
            Db(s) is not { } db ? NoData() : Recipes.Get(db, id) is { } json ? TypedResults.Ok(new RecipeText(json)) : TypedResults.NotFound());
        // Lo de cada tarjeta (#108): última release, si el patrón calza y si está publicada. Sin la clave o sin red, main queda sin leer.
        api.MapGet("/recipes/status", async Task<Results<Ok<List<RecipeStatus>>, Conflict<ApiError>>> (AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            string? main = null;
            if (Publisher(db, s, box) is { } publisher)
                try { main = await publisher.MainRecipes(ct); }
                catch (Exception e) when (e is TaskException or HttpRequestException) { }
            return TypedResults.Ok(await Recipes.Status(db, main, GitHubToken(db, s, box), ct: ct));
        });
        // Descartar lo no publicado (#109): vuelve a la de main; si main no la tiene, se borra de la base local.
        api.MapDelete("/recipes/{id}", async Task<Results<NoContent, Conflict<ApiError>>> (string id, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return Recipes.Discard(db, id, await publisher.MainRecipes(ct)) ? TypedResults.NoContent() : TypedResults.Conflict(ApiError.Of("recipes.noMain")); }
            catch (Exception e) when (e is TaskException or HttpRequestException) { return TypedResults.Conflict(ApiError.Of("recipes.noMain")); }
        });
        // «Probar la descarga» (#109): baja el archivo a una carpeta temporal, informa y la borra.
        api.MapPost("/recipes/{id}/test", async Task<Results<Ok<RecipeTest>, Conflict<ApiError>>> (string id, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            var (result, error) = await Recipes.Test(db, id, GitHubToken(db, s, box), ct);
            return error is null ? TypedResults.Ok(result!) : TypedResults.Conflict(error);
        });
        // Las de main, con sus bytes, mientras la base local no las tenga.
        api.MapPost("/recipes/import", async Task<Results<Ok<int>, Conflict<ApiError>>> (AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(Recipes.Import(db, await publisher.MainRecipes(ct))); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });
        // «Crear desde su repo»: una receta para completar en el formulario; no se guarda.
        api.MapPost("/recipes/draft", async Task<Results<Ok<RecipeText>, BadRequest<ApiError>, Conflict<ApiError>>> (RepoRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            var (json, error) = await Recipes.Draft(request.Url, GitHubToken(db, s, box), ct, request.File);
            return error is null ? TypedResults.Ok(new RecipeText(json!)) : error.Code == "recipes.badRepo" ? TypedResults.BadRequest(error) : TypedResults.Conflict(error);
        });
        // «Agregar una receta»: las releases del repo con sus archivos.
        api.MapGet("/recipes/releases", async Task<Results<Ok<List<RepoRelease>>, BadRequest<ApiError>, Conflict<ApiError>>> (string url, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            var (list, error) = await Recipes.RepoReleases(url, GitHubToken(db, s, box), ct);
            return error is null ? TypedResults.Ok(list!) : error.Code == "recipes.badRepo" ? TypedResults.BadRequest(error) : TypedResults.Conflict(error);
        });
        api.MapPut("/recipes/{id}", async Task<Results<Ok<List<RecipeInfo>>, BadRequest<ApiError>, Conflict<ApiError>>> (string id, RecipeText request, AppSession s, SecretBox box, CancellationToken ct, bool create = false) =>
        {
            if (Db(s) is not { } db) return NoData();
            return await Recipes.Save(db, id, request.Json, create, GitHubToken(db, s, box), ct) is { } error ? TypedResults.BadRequest(error) : TypedResults.Ok(Recipes.List(db));
        });

        // Publicar tu versión como un PR (#22). Solo propone: nada acá fusiona.
        api.MapPost("/publish/preview", async Task<Results<Ok<PublishPreview>, Conflict<ApiError>>> (PublishRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await publisher.Preview(request, ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });
        // Cuántos archivos llevaría «Publicar todo», para la barra de arriba (#85); null si no se puede saber (sin clave o sin conexión).
        api.MapGet("/publish/count", async Task<Ok<int?>> (AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db || Publisher(db, s, box) is not { } publisher) return TypedResults.Ok<int?>(null);
            try { return TypedResults.Ok<int?>(await publisher.CountAll(ct)); }
            catch (Exception e) when (e is TaskException or HttpRequestException) { return TypedResults.Ok<int?>(null); }
        });
        api.MapPost("/publish", async Task<Results<Ok<PublishResult>, Conflict<ApiError>>> (PublishRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await publisher.Publish(request, ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });

        // Ajustes › Catálogo (#105): las versiones publicadas del mismo repo al que se publica, bajar una y elegir sus conflictos.
        api.MapGet("/catalog/versions", async Task<Results<Ok<CatalogVersionsPage>, Conflict<ApiError>>> (AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await new CatalogVersions(db, publisher).Page(ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });
        api.MapPost("/catalog/version/apply", async Task<Results<Ok<CatalogApplyResult>, Conflict<ApiError>>> (CatalogApplyRequest request, AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await new CatalogVersions(db, publisher).Apply(request.Tag, ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });
        api.MapPost("/catalog/catchup", async Task<Results<Ok<CatalogCatchUp>, Conflict<ApiError>>> (AppSession s, SecretBox box, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            if (Publisher(db, s, box) is not { } publisher) return TypedResults.Conflict(ApiError.Of("publish.noKey"));
            try { return TypedResults.Ok(await new CatalogVersions(db, publisher).CatchUp(ct)); }
            catch (TaskException e) { return TypedResults.Conflict(e.Error); }
            catch (HttpRequestException) { return TypedResults.Conflict(ApiError.Of("publish.offline")); }
        });
        api.MapGet("/catalog/conflicts", Results<Ok<List<CatalogConflict>>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(CatalogVersions.Conflicts(db)) : NoData());
        // Elegir un lado; devuelve los que quedan.
        api.MapPost("/catalog/conflicts/{id:long}", Results<Ok<List<CatalogConflict>>, NotFound, Conflict<ApiError>> (long id, ConflictChoice request, AppSession s) =>
            Db(s) is not { } db ? NoData() : CatalogVersions.Resolve(db, id, request.UseRemote) ? TypedResults.Ok(CatalogVersions.Conflicts(db)) : TypedResults.NotFound());

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

    internal static string? GitHubToken(Database db, AppSession s, SecretBox box) =>
        new Credentials(db, s.Config.MachineId, box).Read("github.token") is { Length: > 0 } token ? token : null;

    static GitHubPublisher? Publisher(Database db, AppSession s, SecretBox box) =>
        GitHubToken(db, s, box) is { } token ? new(db, token) : null;

    static bool HasSgdbKey(Database db, AppSession s, SecretBox box) =>
        new Credentials(db, s.Config.MachineId, box).List().Any(c => c.Id == "steamgriddb.apiKey" && c.Saved);

    internal static Database? Db(AppSession s) =>
        DataFolder.IsReady(s.Config.DataFolder) ? DataFolder.Open(s.Config.DataFolder!) : null;

    internal static Conflict<ApiError> NoData() => TypedResults.Conflict(ApiError.Of("move.noDataFolder"));

    static bool IsDate(string? value) =>
        value is null || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

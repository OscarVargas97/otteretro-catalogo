using Microsoft.AspNetCore.Http.HttpResults;
using Otteretro.App;
using Otteretro.Catalog;
using Otteretro.Claude;
using static Otteretro.Api.CatalogEndpoints;

namespace Otteretro.Api;

public record ChooseRouteRequest(long? RouteId);

/// <summary>Las rutas como quedaron al editarlas en Revisar (F2.21).</summary>
public record ApproveRoutesRequest(List<SagaAnalysis.Route> Routes);

/// <summary>Lo que preparó Claude (F2.6): Revisar, rutas de la saga, comparativa de versiones, Inicio › Siguiente y Ajustes › Claude.</summary>
public static class ClaudeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/review", Results<Ok<ReviewPage>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeReview.Page(db)) : NoData());

        // Aprendizajes por tramos al bajar (#177).
        api.MapGet("/learnings", Results<Ok<LearningPage>, Conflict<ApiError>> (AppSession s, string? kind, string? q, int offset = 0, int limit = CatalogQueries.PageSize) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeReview.Learnings(db, kind, q, Math.Max(0, offset), Math.Clamp(limit, 1, 500))) : NoData());

        api.MapPost("/review/{id:long}/approve", Results<Ok<ReviewPage>, NotFound, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is not { } db ? NoData() : ClaudeReview.Approve(db, id) ? TypedResults.Ok(ClaudeReview.Page(db)) : TypedResults.NotFound());

        api.MapPost("/review/{id:long}/approve-edited", Results<Ok<ReviewPage>, NotFound, Conflict<ApiError>> (long id, ApproveRoutesRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : ClaudeReview.Approve(db, id, request.Routes) ? TypedResults.Ok(ClaudeReview.Page(db)) : TypedResults.NotFound());

        api.MapGet("/review/count", Results<Ok<int>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeReview.PendingCount(db)) : NoData());

        // Sugerencias de automatizar y «Crear issue» (§11.5, F2.21).
        api.MapGet("/claude/automation", Results<Ok<List<AutomationSuggestion>>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(Automation.Suggestions(db)) : NoData());

        api.MapPost("/claude/automation/{key}/issue", async Task<Results<Ok<List<AutomationSuggestion>>, BadRequest<ApiError>, Conflict<ApiError>>> (string key, AppSession s, CancellationToken ct) =>
        {
            if (Db(s) is not { } db) return NoData();
            return await Automation.CreateIssue(db, key, ct) is { } error ? TypedResults.BadRequest(error) : TypedResults.Ok(Automation.Suggestions(db));
        });

        api.MapPost("/review/{id:long}/reject", Results<Ok<ReviewPage>, NotFound, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is not { } db ? NoData() : ClaudeReview.Reject(db, id) ? TypedResults.Ok(ClaudeReview.Page(db)) : TypedResults.NotFound());

        api.MapPost("/learnings/{id:long}/undo", Results<Ok<ReviewPage>, NotFound, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is not { } db ? NoData() : CatalogCleanup.Undo(db, id) ? TypedResults.Ok(ClaudeReview.Page(db)) : TypedResults.NotFound());
        // «Corregir» (F2.12): deshace y deja la nota como regla de la saga para la próxima depuración.
        api.MapPost("/learnings/{id:long}/correct", Results<Ok<ReviewPage>, BadRequest<ApiError>, Conflict<ApiError>> (long id, CorrectRequest request, AppSession s) =>
            Db(s) is not { } db ? NoData() : CatalogCleanup.Correct(db, id, request.Note) is { } error ? TypedResults.BadRequest(error) : TypedResults.Ok(ClaudeReview.Page(db)));

        api.MapGet("/sagas/{id:long}/routes", Results<Ok<RoutesPage>, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeReview.Routes(db, id)) : NoData());

        // Pedir a Claude que recalcule la saga: olvida sus huellas y la encola.
        // «Reevaluar sagas icónicas» (F2.15): de todo el catálogo o de una consola (?console=).
        api.MapPost("/claude/iconic", Results<Ok<long>, NotFound, Conflict<ApiError>> (AppSession s, string? console) =>
            Db(s) is not { } db ? NoData() : SagaAnalysis.EnqueueIconic(db, console) is { } id ? TypedResults.Ok(id) : TypedResults.NotFound());

        api.MapPost("/sagas/{id:long}/recalculate", Results<Ok<long>, Conflict<ApiError>> (long id, AppSession s) =>
        {
            if (Db(s) is not { } db) return NoData();
            foreach (var prompt in new[] { SagaAnalysis.Routes, SagaAnalysis.Iconic }) ClaudeFingerprint.Forget(db, prompt, $"saga:{id}");
            return TypedResults.Ok(SagaAnalysis.Enqueue(db, [id]));
        });

        api.MapGet("/games/{id:long}/compare", Results<Ok<List<VersionCompare>>, Conflict<ApiError>> (long id, AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeReview.Compare(db, id)) : NoData());

        api.MapGet("/claude", Results<Ok<ClaudeStatus>, Conflict<ApiError>> (AppSession s) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeBudget.Status(db)) : NoData());

        api.MapGet("/claude/usage", Results<Ok<UsagePage>, Conflict<ApiError>> (AppSession s, int page = 1) =>
            Db(s) is { } db ? TypedResults.Ok(ClaudeBudget.Usage(db, page)) : NoData());

        api.MapPut("/claude", Results<Ok<ClaudeStatus>, BadRequest<ApiError>, Conflict<ApiError>> (ClaudeSettingsRequest request, AppSession s) =>
        {
            if (Db(s) is not { } db) return NoData();
            return ClaudeBudget.Save(db, request) is { } error ? TypedResults.BadRequest(error) : TypedResults.Ok(ClaudeBudget.Status(db));
        });

        api.MapPost("/claude/run-now", Results<Ok<ClaudeStatus>, Conflict<ApiError>> (AppSession s) =>
        {
            if (Db(s) is not { } db) return NoData();
            ClaudeBudget.RunNow(db);
            return TypedResults.Ok(ClaudeBudget.Status(db));
        });
    }
}

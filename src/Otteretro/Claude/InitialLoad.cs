using System.Text.Json;
using Dapper;
using Otteretro.Api;
using Otteretro.App;
using Otteretro.Data;

namespace Otteretro.Claude;

/// <summary>
/// Carga inicial con Claude, por partes (F2.8, #72): Nintendo → Sony → Microsoft → PC (#69, #131). En cada parte primero se
/// depura (F2.7) y después se preparan rutas, icónico y mejor versión (F2.5). Es una sola tarea con su punto de control:
/// al llegar al límite se pausa y sigue sola cuando se renueva, durante los días que haga falta.
/// </summary>
public static class InitialLoad
{
    public const string Kind = "claude.load";

    /// <summary>Las partes en orden: id de la CLI y nombre de la compañía (`console.company`; PC no tiene).</summary>
    public static readonly (string Id, string Name)[] Parts = [("nintendo", "Nintendo"), ("sony", "Sony"), ("microsoft", "Microsoft"), ("pc", "PC")];

    /// <param name="Only">Solo esa parte (`--parte`), sin seguir con las siguientes.</param>
    record Job(int Part, bool Analyzing, string? Inner, bool Only);

    /// <summary>Encola la carga entera, o solo una parte. Null si la parte no existe.</summary>
    public static long? Enqueue(Database db, string? part = null)
    {
        var index = part is null ? 0 : Array.FindIndex(Parts, p => p.Id == part);
        return index < 0 ? null : TaskQueue.Enqueue(db, Kind, JsonSerializer.Serialize(new Job(index, false, null, part is not null), JsonSerializerOptions.Web));
    }

    public static async Task Run(TaskRun run, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<Job>(run.Data ?? "{}", JsonSerializerOptions.Web)!;
        while (job.Part < Parts.Length)
        {
            var (_, name) = Parts[job.Part];
            var sagas = PartSagas(run.Db, job.Part);

            // El avance total: cada parte son dos mitades (depurar y preparar). El detalle dice la parte y cómo va.
            void Report(int progress, ApiError detail, string inner)
            {
                job = job with { Inner = inner };
                var total = job.Only ? 2 : Parts.Length * 2;
                var step = (job.Only ? 0 : job.Part * 2) + (job.Analyzing ? 1 : 0);
                run.Report((step * 100 + progress) / total, new ApiError(detail.Code.Replace("tasks.claude.", "tasks.claude.load."),
                    new(detail.Params ?? []) { ["part"] = name }), JsonSerializer.Serialize(job, JsonSerializerOptions.Web));
            }

            if (!job.Analyzing)
            {
                await CatalogCleanup.Process(run, job.Inner, sagas, Report, ct);
                job = job with { Analyzing = true, Inner = null };
                // La depuración pudo crear sagas nuevas (sub-sagas): entran al análisis de esta parte.
                sagas = PartSagas(run.Db, job.Part);
            }
            await SagaAnalysis.Process(run, job.Inner, sagas, Report, ct);
            if (job.Only) break;
            job = new Job(job.Part + 1, false, null, false);
            run.Report(job.Part * 100 / Parts.Length, null, JsonSerializer.Serialize(job, JsonSerializerOptions.Web));
        }
    }

    /// <summary>Las sagas de una parte: cada saga va en la primera parte en la que tiene un juego.</summary>
    static HashSet<long> PartSagas(Database db, int part)
    {
        using var c = db.Open();
        return [.. c.Query<long>("""
            SELECT g.saga_id FROM game g JOIN version v ON v.game_id = g.id JOIN console k ON k.id = v.platform
            WHERE g.saga_id IS NOT NULL
            GROUP BY g.saga_id
            HAVING MIN(CASE k.company WHEN 'Nintendo' THEN 0 WHEN 'Sony' THEN 1 WHEN 'Microsoft' THEN 2 ELSE 3 END) = @part
            """, new { part })];
    }
}

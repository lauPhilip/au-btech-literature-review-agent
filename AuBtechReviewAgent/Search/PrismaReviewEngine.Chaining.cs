using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>
    /// Citation chaining (one round of backward and forward snowballing): for every included record with a
    /// DOI, the works it cites and the works citing it are looked up in OpenAlex, run through the same
    /// duplicate and year checks, and screened like any other record. They are reported as "identified via
    /// other methods" in the PRISMA flow. The most-cited included studies are built out first and wider
    /// (<see cref="ChainFromSeedsAsync"/>); every other included record with a DOI is chained as before.
    /// </summary>
    private async Task ChainCitationsAsync(RunContext ctx, int yearFrom, int yearTo)
    {
        var graph = CitationGraphFactory?.Invoke() ?? new OpenAlexSource(OpenSources.ContactEmail, OpenSources.OpenAlexApiKey);
        var builtOut = await ChainFromSeedsAsync(ctx, graph, yearFrom, yearTo);

        var seeds = ctx.State.Phases.Screening
            .Where(l => l.Decision == "Included" && l.Origin == OriginDatabase && NormalizeDoi(l.Doi) != null && !builtOut.Contains(l.PaperId))
            .ToList();
        if (seeds.Count == 0) return;

        int perDirection = Math.Clamp(OpenSources.ChainingPerPaper, 1, 25);
        string sourceName = $"{graph.SourceName} (citation chaining)";

        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        async Task<(ScreeningLog Seed, CitationNeighbours? Result, string? Error)> LookUp(ScreeningLog seed)
        {
            await throttle.WaitAsync();
            try { return (seed, await graph.GetCitationNeighboursAsync(NormalizeDoi(seed.Doi)!, perDirection), null); }
            catch (Exception ex) { return (seed, null, SanitizeLogMessage(ex.Message)); }
            finally { throttle.Release(); }
        }
        ReportProgress(ctx.RunId, ctx.State, RunProgress.Chaining, 0, $"Looking up references and citing papers of {seeds.Count} included stud{(seeds.Count == 1 ? "y" : "ies")}");
        var lookups = seeds.Select(LookUp).ToList();

        var candidates = new List<(AcademicPaper Paper, string SourceName)>();
        for (int i = 0; i < lookups.Count; i++)
        {
            var (seed, result, error) = await lookups[i];
            ReportProgress(ctx.RunId, ctx.State, RunProgress.Chaining, 0.3 * (i + 1) / lookups.Count, Of(i + 1, lookups.Count, "look-ups"));
            var log = new PlatformSearchLog
            {
                SourceName = sourceName,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                QueryUsed = $"references of and citations to doi:{NormalizeDoi(seed.Doi)}",
                Status = error == null ? "Completed Successfully" : "Faulted / Refused Connection",
                ErrorMessage = error ?? "None",
            };
            var found = result == null ? new List<AcademicPaper>() : result.Backward.Concat(result.Forward).ToList();
            log.PapersFound = found.Count;
            if (result != null) SaveRawResponses(ctx, log, result.RawResponses, $"chain-{i + 1:00}");
            ctx.State.SearchLogs.Add(log);

            ctx.State.Stats.TotalIdentified += found.Count;
            ctx.State.Stats.IdentifiedViaCitations += found.Count;
            foreach (var paper in found)
            {
                if (Admit(ctx, paper, sourceName, log.QueryUsed, yearFrom, yearTo, capReached: false))
                    candidates.Add((paper, sourceName));
            }
        }
        OnProgressUpdated?.Invoke(ctx.RunId, ctx.State.Stats);
        await SaveStateAsync(ctx.RunId, ctx.State);

        await ScreenCandidatesAsync(ctx, candidates, OriginCitations);
    }
}

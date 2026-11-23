using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    public const string OriginSeedChaining = "seed chaining";

    /// <summary>
    /// The seed step of citation chaining: the included database records with the most citations per year since
    /// publication are chosen in code (<see cref="CitationSeeds"/>), only from records already included at screening, so
    /// a highly cited but irrelevant paper is never a seed. Each seed is built out one round in both directions, wider
    /// than ordinary chaining, its neighbours ranked by citations per year; they go through the same duplicate and year
    /// checks and are screened like any other record, with origin "seed chaining". Returns the seeds that were looked up,
    /// which ordinary chaining then skips.
    /// </summary>
    private async Task<HashSet<string>> ChainFromSeedsAsync(RunContext ctx, ICitationGraph graph, int yearFrom, int yearTo)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        int count = Math.Clamp(OpenSources.SeedStudies, 0, 50);
        if (count == 0) return done;

        var included = ctx.State.Phases.Screening
            .Where(l => l.Decision == "Included" && l.Origin == OriginDatabase)
            .Select(l => new SeedCandidate(l.PaperId, l.Title, l.Year, l.CitedByCount, l.Doi));
        int referenceYear = (ctx.State.Stats.RunStartedUtc ?? ctx.State.Timestamp).Year;
        var record = CitationSeeds.Select(included, count, Math.Clamp(OpenSources.SeedChainingPerDirection, 1, CitationSeeds.PoolPerDirection), referenceYear);
        ctx.State.SeedChaining = record;
        if (record.Seeds.Count == 0) return done;

        ReportProgress(ctx.RunId, ctx.State, RunProgress.Chaining, 0, $"Building out from the {record.Seeds.Count} most-cited included stud{(record.Seeds.Count == 1 ? "y" : "ies")}");
        var results = await CitationSeeds.BuildOutAsync(graph, record, SanitizeLogMessage,
            n => ReportProgress(ctx.RunId, ctx.State, RunProgress.Chaining, 0.3 * n / record.Seeds.Count, Of(n, record.Seeds.Count, "seed studies")));

        string sourceName = $"{graph.SourceName} (seed chaining)";
        var candidates = new List<(AcademicPaper Paper, string SourceName)>();
        foreach (var (seed, backward, forward, raw) in results)
        {
            if (seed.Error == null) done.Add(seed.PaperId);
            var log = new PlatformSearchLog
            {
                SourceName = sourceName,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                QueryUsed = $"seed {seed.Rank}: references of and citations to doi:{seed.Doi}, the {record.PerDirection} with the most citations per year in each direction",
                Status = seed.Error == null ? "Completed Successfully" : "Faulted / Refused Connection",
                ErrorMessage = seed.Error ?? "None",
            };
            var found = backward.Concat(forward).ToList();
            log.PapersFound = found.Count;
            SaveRawResponses(ctx, log, raw, $"seed-{seed.Rank:00}");
            ctx.State.SearchLogs.Add(log);

            ctx.State.Stats.TotalIdentified += found.Count;
            ctx.State.Stats.IdentifiedViaCitations += found.Count;
            ctx.State.Stats.IdentifiedViaSeedChaining += found.Count;
            foreach (var paper in found)
                if (Admit(ctx, paper, sourceName, log.QueryUsed, yearFrom, yearTo, capReached: false))
                    candidates.Add((paper, sourceName));
        }
        OnProgressUpdated?.Invoke(ctx.RunId, ctx.State.Stats);
        await SaveStateAsync(ctx.RunId, ctx.State);

        await ScreenCandidatesAsync(ctx, candidates, OriginSeedChaining);
        return done;
    }
}

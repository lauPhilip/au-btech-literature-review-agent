using System;

namespace AuBtechReviewAgent;

// Where a review stands: read from the live counters while it runs here, otherwise from its ledger.
public sealed partial class ReviewApi
{
    /// <summary>The stage, the step of seven, the counts and, once done, the verdicts and the downloads.</summary>
    public ApiResult<ReviewStatus> Status(Guid runId, string baseUrl)
    {
        var header = _engine.LoadHeader(runId);
        if (header == null) return ApiProblems.NoRun();
        if (header.Module != ReviewMethod.SystematicKey) return ApiProblems.NotSystematic();

        var state = _engine.LoadState(runId);
        // Between the start and the first save of the ledger the run has only its folder: it is queued.
        var stats = _engine.LiveStats(runId) ?? state?.Stats ?? new ReviewStats { ProcessingStage = PrismaReviewEngine.StageQueued };
        ReviewStatus status;
        lock (stats)
        {
            string stage = string.IsNullOrEmpty(stats.ProcessingStage) || stats.ProcessingStage == "Idle"
                ? PrismaReviewEngine.StageQueued : stats.ProcessingStage;
            int current = ReviewSteps.Current(stats, stage);
            bool done = stage == PrismaReviewEngine.StageComplete;
            status = new ReviewStatus(
                runId,
                stage,
                current < 0 ? 0 : ReviewSteps.Number(current),
                ReviewSteps.Count,
                StepName(current, stage),
                RunProgress.Percent(stats),
                stats.ProgressDetail ?? "",
                stats.QueuePosition,
                new ReviewCounts(stats.TotalIdentified, stats.Screened, stats.Excluded, stats.Included),
                done ? new ReviewVerdicts(stats.CitationsChecked, stats.CitationsSupported, stats.CitationsPartiallySupported,
                    stats.CitationsNotSupported, stats.CitationsUnverifiable) : null,
                PrismaReviewEngine.IsFinishedStage(stage) ? state?.FailureMessage : null,
                stats.RunStartedUtc ?? header.StartedUtc,
                state?.CompletedUtc,
                LinksFor(runId, baseUrl),
                done ? DownloadsFor(runId, baseUrl) : null);
        }
        return ApiResult<ReviewStatus>.Ok(status);
    }

    /// <summary>The name of the step, as the run view shows it: "Queued", one of the seven, or "Done".</summary>
    private static string StepName(int current, string stage)
    {
        if (current < 0) return "Queued";
        if (current >= ReviewSteps.Count) return stage == PrismaReviewEngine.StageComplete ? "Done" : ReviewSteps.All[ReviewSteps.Count - 1].Title;
        return ReviewSteps.All[current].Title;
    }

    internal static ReviewDownloads DownloadsFor(Guid runId, string baseUrl) => new(
        $"{baseUrl}/api/workspace/{runId}/archive",
        $"{baseUrl}/api/workspace/{runId}/references.bib",
        $"{baseUrl}/api/workspace/{runId}/references.ris",
        $"{baseUrl}/api/workspace/{runId}/screened.ris",
        $"{baseUrl}/api/workspace/{runId}/protocol.md");
}

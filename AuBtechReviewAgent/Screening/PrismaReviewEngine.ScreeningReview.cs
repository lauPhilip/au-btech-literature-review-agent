using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>
    /// Pauses the run until the user has confirmed or changed the screening decisions in the dashboard.
    /// Every confirmation and change is written to the ledger (ModelDecision keeps what the model said).
    /// If nobody responds within Runs:ScreeningReviewTimeoutHours, the model's decisions are kept and the
    /// ledger says that no human review took place.
    /// </summary>
    private async Task AwaitScreeningReviewAsync(RunContext ctx)
    {
        var state = ctx.State;
        // Save the "awaiting review" stage first and only then open the gate, so anyone who sees the gate
        // open (the dashboard, a test) also finds the ledger already saying that the run is waiting.
        state.Stats.ProcessingStage = StageAwaitingReview;
        ReportProgress(ctx.RunId, state, RunProgress.Review, 0, "Waiting for your screening decisions");
        await PublishAsync(ctx);
        Coordinator.OpenScreeningReview(ctx.RunId);

        var decisions = await Coordinator.WaitForScreeningReviewAsync(ctx.RunId, TimeSpan.FromHours(Math.Max(1, _runsOptions.ScreeningReviewTimeoutHours)));
        if (decisions == null)
        {
            state.HumanScreeningReviewOutcome = $"No review was submitted within {_runsOptions.ScreeningReviewTimeoutHours} hours; the model's screening decisions were kept unchanged.";
            return;
        }

        ApplyScreeningReview(state, decisions);
        state.HumanScreeningReviewOutcome = $"A human reviewer checked {state.Stats.HumanReviewed} screening decision(s) and changed {state.Stats.HumanOverrides}.";
        await PublishAsync(ctx);
    }

    /// <summary>Applies a reviewer's decisions to the ledger and the PRISMA counters. Public for unit tests.</summary>
    public static void ApplyScreeningReview(ReviewState state, IReadOnlyList<ScreeningOverride> decisions)
    {
        var byId = decisions
            .Where(d => d.Decision is "Included" or "Excluded")
            .GroupBy(d => d.PaperId)
            .ToDictionary(g => g.Key, g => g.Last());

        for (int i = 0; i < state.Phases.Screening.Count; i++)
        {
            var log = state.Phases.Screening[i];
            if (log.Decision is not ("Included" or "Excluded")) continue;
            if (!byId.TryGetValue(log.PaperId, out var d)) continue;

            state.Stats.HumanReviewed++;
            string? note = string.IsNullOrWhiteSpace(d.Note) ? null : d.Note.Trim();
            if (d.Decision == log.Decision)
            {
                state.Phases.Screening[i] = log with { HumanReviewed = true, HumanNote = note };
                continue;
            }

            state.Stats.HumanOverrides++;
            bool wasPeerReviewFilter = log.BriefSummary == "N/A" && log.Reasoning.StartsWith("Excluded during post-retrieval pre-screening", StringComparison.Ordinal);
            if (d.Decision == "Included")
            {
                state.Stats.Included++;
                if (wasPeerReviewFilter) state.Stats.FailedPeerReviewCheck--; else state.Stats.Excluded--;
            }
            else
            {
                state.Stats.Included--;
                state.Stats.Excluded++;
            }

            state.Phases.Screening[i] = log with
            {
                ModelDecision = log.Decision,
                Decision = d.Decision,
                ExclusionReason = d.Decision == "Excluded" ? ExclusionReasons.ByReviewer : null,
                HumanReviewed = true,
                HumanNote = note,
                BriefSummary = log.BriefSummary == "N/A" ? "(Included by the human reviewer; no model summary was produced.)" : log.BriefSummary,
            };
        }
    }
}

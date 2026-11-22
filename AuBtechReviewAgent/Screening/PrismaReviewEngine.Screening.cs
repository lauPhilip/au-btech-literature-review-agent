using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Screening stage: peer-review filter, (dual) screening of each record and the human review step.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// Part of the screening cache key: change it whenever a screening prompt changes, so decisions made
    /// with an older prompt are never reused.
    /// </summary>
    public const string ScreeningPromptVersion = "screening-v6";

    private sealed record ScreeningOutcome(
        AcademicPaper Paper,
        string SourceName,
        bool FailedPeerReview,
        ScreeningAnswer? First,
        ScreeningAnswer? Second,
        int CacheHits,
        IReadOnlyList<string> Flags,
        string? Error);

    /// <summary>
    /// Screens the candidates several at a time (Llm:ScreeningParallelism), but applies the results to the
    /// ledger strictly in candidate order, so the ledger and counters come out the same on every run.
    /// </summary>
    private async Task ScreenCandidatesAsync(RunContext ctx, List<(AcademicPaper Paper, string SourceName)> candidates, string origin)
    {
        if (candidates.Count == 0) return;
        foreach (var (paper, _) in candidates) ctx.Papers[paper.Id] = paper;

        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        var work = candidates.Select(async c =>
        {
            await throttle.WaitAsync();
            try { return await ScreenOneAsync(ctx, c.Paper, c.SourceName); }
            finally { throttle.Release(); }
        }).ToList();

        bool chaining = origin == OriginCitations;
        int applied = 0;
        foreach (var task in work)
        {
            var outcome = await task;
            ApplyOutcome(ctx, outcome, origin);
            applied++;
            ReportProgress(ctx.RunId, ctx.State, chaining ? RunProgress.Chaining : RunProgress.Screening,
                chaining ? 0.3 + 0.7 * applied / work.Count : (double)applied / work.Count, Of(applied, work.Count, "records screened"));
            OnProgressUpdated?.Invoke(ctx.RunId, ctx.State.Stats);
            await SaveStateAsync(ctx.RunId, ctx.State);
        }
    }

    private async Task<ScreeningOutcome> ScreenOneAsync(RunContext ctx, AcademicPaper paper, string sourceName)
    {
        var flags = PromptSafety.Scan(paper.Title, paper.Abstract, paper.JournalSource);
        try
        {
            if (ctx.State.PeerReviewOnlyToggle)
            {
                bool peerReviewed;
                using (LlmStage.Begin("peer-review-filter"))
                    peerReviewed = await IsPeerReviewedAsync(ctx.Chat, sourceName, paper);
                if (!peerReviewed) return new ScreeningOutcome(paper, sourceName, true, null, null, 0, flags, null);
            }

            // The two screenings are independent (different prompts, separate cache keys), so they run at the same
            // time; each call keeps its own stage label, which is taken when the call starts.
            Task<(ScreeningAnswer Answer, bool FromCache)> firstTask;
            Task<(ScreeningAnswer Answer, bool FromCache)>? secondTask = null;
            using (LlmStage.Begin("screening"))
                firstTask = ScreenWithCacheAsync(ctx, paper, secondScreener: false);
            if (ctx.Request.DualScreening)
                using (LlmStage.Begin("screening-second"))
                    secondTask = ScreenWithCacheAsync(ctx, paper, secondScreener: true);

            if (secondTask == null)
            {
                var (only, onlyHit) = await firstTask;
                return new ScreeningOutcome(paper, sourceName, false, only, null, onlyHit ? 1 : 0, flags, null);
            }
            await Task.WhenAll(firstTask, secondTask); // a failure of either is thrown here, and both are observed
            var (first, firstHit) = await firstTask;
            var (second, secondHit) = await secondTask;
            return new ScreeningOutcome(paper, sourceName, false, first, second, (firstHit ? 1 : 0) + (secondHit ? 1 : 0), flags, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run {RunId}: screening failed for '{Title}': {Message}", ctx.RunId, paper.Title, SanitizeLogMessage(ex.Message));
            return new ScreeningOutcome(paper, sourceName, false, null, null, 0, flags, SanitizeLogMessage(ex.Message));
        }
    }

    private async Task<(ScreeningAnswer Answer, bool FromCache)> ScreenWithCacheAsync(RunContext ctx, AcademicPaper paper, bool secondScreener)
    {
        var r = ctx.Request;
        // The model that makes this screening, so a decision is never reused under another model.
        string key = ReviewCache.Key(ScreeningPromptVersion, secondScreener ? "second" : "first", Llm.ModelFor(secondScreener ? "screening-second" : "screening"),
            r.Inclusion, r.Exclusion, paper.Title, paper.Abstract, paper.JournalSource, paper.PublishedDate, string.Join(";", paper.Authors ?? new()));
        if (Cache.TryGet<ScreeningAnswer>("screening", key, out var cached) && cached != null && ScreeningAnswer.Validate(cached) == null)
            return (cached.Normalized(), true);

        var answer = await ScreenPaperAsync(ctx.Chat, paper, r.Inclusion, r.Exclusion, secondScreener);
        Cache.Set("screening", key, answer);
        return (answer, false);
    }

    private void ApplyOutcome(RunContext ctx, ScreeningOutcome o, string origin)
    {
        var state = ctx.State;
        var stats = state.Stats;
        var paper = o.Paper;
        var apa = ApaCitationBuilder.Build(paper);
        var flags = o.Flags.Count > 0 ? o.Flags.ToList() : null;
        if (flags != null) stats.InjectionSuspected++;
        stats.CacheHits += o.CacheHits;

        ScreeningLog Log(string decision, string reasoning, string summary) => new(
            paper.Id, paper.Title, decision, reasoning, apa.Citation, summary,
            apa.VenueType, apa.VenueName, apa.Year, paper.Authors, paper.Doi, paper.Url, paper.Abstract,
            InjectionFlags: flags, Origin: origin);

        if (o.Error != null)
        {
            // Not silently dropped: the record is logged as not screened and counted.
            stats.ScreeningErrors++;
            state.Phases.Screening.Add(Log("Error", $"Not screened: the model call failed ({o.Error}).", "N/A"));
            return;
        }

        if (o.FailedPeerReview)
        {
            stats.FailedPeerReviewCheck++;
            stats.Screened++;
            state.Phases.Screening.Add(Log("Excluded",
                "Excluded during post-retrieval pre-screening: Document was classified as an un-reviewed preprint or working paper, violating the active Peer-Reviewed Only configuration threshold.",
                "N/A") with { ExclusionReason = ExclusionReasons.NotPeerReviewed });
            return;
        }
        if (state.PeerReviewOnlyToggle) stats.PassedPeerReviewCheck++;

        var first = o.First!;
        var second = o.Second;
        bool agree = second == null || second.IsIncluded == first.IsIncluded;
        // Disagreement is resolved towards inclusion, as in human dual screening: the record goes on and a
        // person is asked to look at it first. Excluding it would hide the disagreement.
        string decision = agree ? first.Decision : "Included";
        string reasoning = agree
            ? first.Reasoning
            : $"The two independent screenings disagreed (first: {first.Decision}, second: {second!.Decision}), so the record was kept and flagged for human attention. First screening: {first.Reasoning}";
        string? confidence = LowestConfidence(first.Confidence, second?.Confidence);
        // A record without an abstract was judged on its title alone (in a real run the model then claimed
        // the missing abstract "explicitly mentions" the criteria), so the decision is marked low-confidence.
        bool noAbstract = string.IsNullOrWhiteSpace(paper.Abstract);
        if (noAbstract)
        {
            confidence = "low";
            reasoning = "(No abstract was available, so this decision rests on the title and venue only.) " + reasoning;
        }
        bool uncertain = !agree || confidence == "low" || flags != null;

        stats.Screened++;
        if (decision == "Included") stats.Included++; else stats.Excluded++;
        if (second != null)
        {
            stats.DualScreened++;
            ctx.DualPairs.Add((first.IsIncluded, second.IsIncluded));
            if (!agree) stats.ScreeningDisagreements++;
        }
        if (uncertain) stats.UncertainDecisions++;

        state.Phases.Screening.Add(Log(decision, reasoning, first.BriefSummary) with
        {
            SecondDecision = second?.Decision,
            SecondReasoning = second?.Reasoning,
            Confidence = confidence,
            Uncertain = uncertain,
            FromCache = o.CacheHits > 0 && o.CacheHits == (second == null ? 1 : 2),
            ExclusionReason = decision == "Excluded" ? first.ExclusionReason ?? second?.ExclusionReason ?? ExclusionReasons.Unspecified : null,
        });
    }

    private static string? LowestConfidence(params string?[] values)
    {
        var known = values.Where(v => v != null).ToList();
        if (known.Count == 0) return null;
        return known.Contains("low") ? "low" : known.Contains("medium") ? "medium" : "high";
    }

    /// <summary>Cohen's kappa between the two screenings, once all records are screened.</summary>
    private static void FinishDualScreeningStats(RunContext ctx)
    {
        if (ctx.DualPairs.Count == 0) return;
        ctx.State.Stats.ScreeningKappa = Math.Round(ScreeningConfusion.From(ctx.DualPairs).CohensKappa, 3);
    }
}

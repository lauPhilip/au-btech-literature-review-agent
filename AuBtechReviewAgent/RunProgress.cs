using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The steps a run goes through as the dashboard shows them, how much of the run each one takes, and the
/// overall percentage. The engine sets the current step, how far it is (0 to 1) and a short detail in
/// <see cref="ReviewStats"/>; the percentage is computed from those, so it only moves forward.
/// </summary>
public static class RunProgress
{
    public const string Search = "search";
    public const string Screening = "screening";
    public const string Chaining = "chaining";
    public const string Review = "review";
    public const string FullText = "fulltext";
    public const string Extraction = "extraction";
    public const string Synthesis = "synthesis";
    public const string Checking = "checking";
    public const string Done = "done";

    /// <summary>Label and rough share of the run's time for every step.</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, int Weight)> Steps = new Dictionary<string, (string, int)>
    {
        [Search] = ("Search", 8),
        [Screening] = ("Screening", 25),
        [Chaining] = ("Citation chaining", 12),
        [Review] = ("Your screening review", 2),
        [FullText] = ("Full texts", 8),
        [Extraction] = ("Extraction and appraisal", 15),
        [Synthesis] = ("Writing", 25),
        [Checking] = ("Citation checks", 15),
    };

    /// <summary>The steps of one run, in order; the optional ones only when they were asked for.</summary>
    public static List<string> Plan(bool citationChaining, bool humanReview)
    {
        var plan = new List<string> { Search, Screening };
        if (citationChaining) plan.Add(Chaining);
        if (humanReview) plan.Add(Review);
        plan.AddRange(new[] { FullText, Extraction, Synthesis, Checking });
        return plan;
    }

    /// <summary>Overall progress from 0 to 100.</summary>
    public static int Percent(ReviewStats stats)
    {
        if (stats.ProgressStep == Done || stats.ProcessingStage == PrismaReviewEngine.StageComplete) return 100;
        var plan = stats.ProgressPlan.Count > 0 ? stats.ProgressPlan : Plan(false, false);
        int total = plan.Sum(s => Steps.TryGetValue(s, out var d) ? d.Weight : 0);
        int index = plan.IndexOf(stats.ProgressStep);
        if (total == 0 || index < 0) return 0;
        double done = plan.Take(index).Sum(s => Steps[s].Weight) + Steps[plan[index]].Weight * Math.Clamp(stats.ProgressFraction, 0, 1);
        return (int)Math.Floor(done * 100 / total);
    }

    /// <summary>"done", "current" or "pending" for one step of the plan.</summary>
    public static string StateOf(ReviewStats stats, string step)
    {
        if (stats.ProgressStep == Done || stats.ProcessingStage == PrismaReviewEngine.StageComplete) return "done";
        int current = stats.ProgressPlan.IndexOf(stats.ProgressStep), index = stats.ProgressPlan.IndexOf(step);
        if (current < 0 || index < 0) return "pending";
        return index < current ? "done" : index == current ? "current" : "pending";
    }

    /// <summary>Fraction of a step made of phases: phase <paramref name="phase"/> of the given boundaries, <paramref name="within"/> done.</summary>
    public static double Phase(double[] boundaries, int phase, double within) =>
        boundaries[phase] + (boundaries[phase + 1] - boundaries[phase]) * Math.Clamp(within, 0, 1);

    // Writing: outline and draft, coding, codebook, theme subsections, discussion, peer review.
    public static readonly double[] SynthesisPhases = { 0, 0.15, 0.45, 0.5, 0.85, 0.92, 1 };
    // Citation checks: first check, second check, repair.
    public static readonly double[] CheckingPhases = { 0, 0.6, 0.75, 1 };
}

public partial class PrismaReviewEngine
{
    // When each run last sent a progress event, so a fast loop sends a few updates per second, not hundreds.
    private readonly ConcurrentDictionary<Guid, long> _lastProgressTicks = new();
    private static readonly long ProgressInterval = TimeSpan.FromMilliseconds(400).Ticks;

    /// <summary>
    /// Sets the run's current step, how far it is and a short detail, and tells the dashboard. Only the
    /// in-memory counters change here; the ledger is saved at the usual points, so this is cheap to call
    /// from inside loops. A step change is always sent; updates within a step at most every 400 ms.
    /// </summary>
    private void ReportProgress(Guid runId, ReviewState state, string step, double fraction, string detail = "")
    {
        var stats = state.Stats;
        bool stepChanged;
        lock (stats)
        {
            stepChanged = stats.ProgressStep != step;
            // Within a step the bar never goes back (parallel work can report out of order).
            double value = Math.Clamp(fraction, 0, 1);
            stats.ProgressFraction = stepChanged ? value : Math.Max(stats.ProgressFraction, value);
            stats.ProgressStep = step;
            stats.ProgressDetail = detail;
        }
        long now = DateTime.UtcNow.Ticks;
        long last = _lastProgressTicks.GetOrAdd(runId, 0);
        if (!stepChanged && now - last < ProgressInterval) return;
        _lastProgressTicks[runId] = now;
        OnProgressUpdated?.Invoke(runId, stats);
    }

    private static string Of(int done, int total, string noun) => $"{done} of {total} {noun}";
}

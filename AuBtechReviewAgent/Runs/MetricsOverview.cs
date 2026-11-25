using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The figures of the Metrics page (<c>Components/Pages/Metrics.razor</c>) for one kind of review, computed in code
/// from the stored run metrics: the four headline figures, one row per run, the totals and the groups by app version
/// and settings. Every share is pooled over citations or studies, never an average of the runs' own shares, so a
/// short run does not count as much as a long one.
/// </summary>
public sealed class MetricsOverview
{
    /// <summary>The runs, oldest first, as the chart draws them.</summary>
    public IReadOnlyList<RunMetrics> Runs { get; }

    /// <summary>The final verdicts of every run, added up.</summary>
    public VerdictCounts Pooled { get; }

    /// <summary>The citations the second repair pass removed in every run, counted apart from the verdicts.</summary>
    public int Removed { get; }

    /// <summary>The included studies (kept sources) of every run, and how many of them the text cites.</summary>
    public int Included { get; }
    public int StudiesCited { get; }

    /// <summary>The share of included studies that the text cites, over all runs.</summary>
    public double CoverageShare => Included == 0 ? 0 : (double)StudiesCited / Included;

    /// <summary>The runs grouped by app version and settings, the group with the latest run first.</summary>
    public IReadOnlyList<VersionGroup> Groups { get; }

    private MetricsOverview(IReadOnlyList<RunMetrics> runs)
    {
        Runs = runs;
        Pooled = Pool(runs.Select(r => r.CitationsFinal));
        Removed = runs.Sum(r => r.Removed);
        Included = runs.Sum(r => r.Included);
        StudiesCited = runs.Sum(r => r.StudiesCited);
        Groups = runs.GroupBy(r => (r.ShortVersion, r.ConfigFingerprint))
            .Select(g => new VersionGroup(
                g.Key.ShortVersion, g.Key.ConfigFingerprint,
                string.Join(", ", g.First().Settings.Select(kv => $"{kv.Key}={kv.Value}")) + $", model={g.First().Model}",
                g.Count(), Pool(g.Select(r => r.CitationsFinal)),
                Mean(g.Select(r => r.ScreeningKappa)), g.Average(r => r.DurationMinutes),
                g.Max(r => r.CompletedUtc) ?? "") { Removed = g.Sum(r => r.Removed), Members = g.ToList() })
            .OrderByDescending(g => g.LatestUtc, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The overview of the given runs, sorted oldest first.</summary>
    public static MetricsOverview From(IEnumerable<RunMetrics> runs) =>
        new(runs.OrderBy(r => r.CompletedUtc, StringComparer.Ordinal).ToList());

    /// <summary>The mean screening kappa over the runs that have one, or null.</summary>
    public double? ScreeningKappa => Mean(Runs.Select(r => r.ScreeningKappa));

    /// <summary>The mean duration of a run, in minutes.</summary>
    public double MinutesPerRun => Runs.Count == 0 ? 0 : Runs.Average(r => r.DurationMinutes);

    /// <summary>The words of a run's written results and discussion.</summary>
    public static int Words(RunMetrics r) => r.SynthesisWords + r.DiscussionWords;

    /// <summary>The first eight characters of a run id, as the chart's x-axis and the tables show it.</summary>
    public static string ShortId(string runId) => runId.Length <= 8 ? runId : runId[..8];

    /// <summary>The completion time as "2026-11-20 14:03" (UTC), or the stored text when it cannot be read.</summary>
    public static string Completed(string completedUtc) =>
        DateTime.TryParse(completedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : completedUtc;

    /// <summary>Adds up verdict counts.</summary>
    public static VerdictCounts Pool(IEnumerable<VerdictCounts> counts)
    {
        var list = counts.ToList();
        return new VerdictCounts
        {
            Total = list.Sum(c => c.Total), Supported = list.Sum(c => c.Supported), PartiallySupported = list.Sum(c => c.PartiallySupported),
            NotSupported = list.Sum(c => c.NotSupported), InsufficientEvidence = list.Sum(c => c.InsufficientEvidence), Unverifiable = list.Sum(c => c.Unverifiable),
        };
    }

    /// <summary>
    /// The five verdicts of one run as stacked segments of a 100% bar, from the bottom up: supported, partly,
    /// not supported, abstract only (or not judged from the kept text), could not be verified. Each segment has its
    /// start and height as a share of the bar (0 to 1); verdicts with no citations are left out.
    /// </summary>
    public static IReadOnlyList<BarSegment> Segments(VerdictCounts c)
    {
        var list = new List<BarSegment>();
        if (c.Total <= 0) return list;
        double start = 0;
        foreach (var (verdict, count) in new[]
                 {
                     (Verdict.Supported, c.Supported), (Verdict.Partly, c.PartiallySupported), (Verdict.NotSupported, c.NotSupported),
                     (Verdict.AbstractOnly, c.InsufficientEvidence), (Verdict.Unverifiable, c.Unverifiable),
                 })
        {
            if (count <= 0) continue;
            double share = (double)count / c.Total;
            list.Add(new BarSegment(verdict, start, share));
            start += share;
        }
        return list;
    }

    /// <summary>A share as a whole percentage, "75%".</summary>
    public static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A kappa with two decimals, or "–" when there is none.</summary>
    public static string Kappa(double? k) => k is double v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "–";

    private static double? Mean(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    /// <summary>The five verdicts in the order the bars stack them.</summary>
    public enum Verdict { Supported, Partly, NotSupported, AbstractOnly, Unverifiable }

    /// <summary>One verdict's part of a run's bar: where it starts and how tall it is, as shares of the bar.</summary>
    public readonly record struct BarSegment(Verdict Verdict, double Start, double Share);

    /// <summary>The runs made with one app version and one set of settings, and the citations their repair removed.</summary>
    public sealed record VersionGroup(string Version, string Fingerprint, string SettingsText, int Runs, VerdictCounts Final,
        double? ScreeningKappa, double MinutesPerRun, string LatestUtc)
    {
        public int Removed { get; init; }

        /// <summary>The group's runs, for figures the page works out itself, such as the estimated cost per run.</summary>
        public IReadOnlyList<RunMetrics> Members { get; init; } = Array.Empty<RunMetrics>();
    }
}

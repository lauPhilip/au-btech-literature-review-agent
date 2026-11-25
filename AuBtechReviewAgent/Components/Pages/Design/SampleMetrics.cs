using System;
using System.Collections.Generic;
using System.Globalization;

namespace AuBtechReviewAgent;

/// <summary>
/// Made-up run metrics for the development page /design/metrics, so the accessibility job in CI can check the
/// Metrics page with a full chart and full tables (the real store is empty there). Fourteen systematic reviews over
/// three app versions and four grey literature reviews; every verdict appears, and one run has no themes.
/// Nothing here is a real result.
/// </summary>
public static class SampleMetrics
{
    // Supported, partly, not supported, abstract only (or not judged), could not be verified.
    private static readonly int[][] Systematic =
    {
        new[] { 41, 9, 6, 3, 1 }, new[] { 52, 8, 4, 5, 0 }, new[] { 38, 11, 7, 2, 2 }, new[] { 60, 7, 3, 4, 1 },
        new[] { 47, 6, 5, 0, 0 }, new[] { 55, 9, 2, 6, 3 }, new[] { 63, 5, 4, 2, 0 }, new[] { 49, 10, 3, 3, 1 },
        new[] { 71, 6, 2, 1, 0 }, new[] { 58, 4, 3, 5, 2 }, new[] { 66, 8, 1, 2, 0 }, new[] { 74, 5, 2, 3, 1 },
        new[] { 69, 3, 2, 0, 0 }, new[] { 80, 6, 1, 4, 1 },
    };

    private static readonly int[][] GreyLiterature =
    {
        new[] { 22, 6, 4, 3, 2 }, new[] { 28, 5, 2, 4, 1 }, new[] { 31, 4, 3, 2, 0 }, new[] { 35, 3, 1, 2, 1 },
    };

    private static readonly string[] Versions = { "0.3.0+1a2b3c4", "0.4.0+5d6e7f8", "0.5.0-redesign.9+9a8b7c6" };

    /// <summary>The sample runs of both kinds, oldest first.</summary>
    public static IReadOnlyList<RunMetrics> Runs()
    {
        var list = new List<RunMetrics>();
        var start = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < Systematic.Length; i++)
            list.Add(Run(i, Systematic[i], start.AddDays(i * 5.0).AddHours(i % 7.0), Versions[Math.Min(2, i / 5)], grey: false));
        for (int i = 0; i < GreyLiterature.Length; i++)
            list.Add(Run(100 + i, GreyLiterature[i], start.AddDays(40 + i * 9.0), Versions[Math.Min(2, 1 + i / 2)], grey: true));
        return list;
    }

    private static RunMetrics Run(int n, int[] v, DateTime completed, string version, bool grey)
    {
        int total = v[0] + v[1] + v[2] + v[3] + v[4];
        int included = grey ? 18 + n % 5 : 12 + n % 9;
        bool thematic = n != 4;
        var settings = new Dictionary<string, string> { ["DualCoding"] = "true", ["SecondCheck"] = n % 3 == 0 ? "false" : "true" };
        if (grey) settings["Module"] = "multivocal";
        string fingerprint = RunMetrics.Sha(version + string.Join("|", settings));
        return new RunMetrics
        {
            RunId = Guid.Parse($"5a3e{n:x4}-0000-4000-8000-{n:x12}").ToString("N"),
            CompletedUtc = completed.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            AppVersion = version,
            Model = "mistral-medium-latest",
            ConfigFingerprint = fingerprint,
            Settings = settings,
            Identified = 180 + n * 23 % 260,
            DuplicatesRemoved = 20 + n % 30,
            Screened = 150 + n * 17 % 200,
            Included = included,
            ScreeningKappa = 0.61 + n % 7 * 0.04,
            FullTextRetrieved = included - n % 4,
            StudiesCited = included - n % 3 - 1,
            ThematicSynthesisUsed = thematic,
            Themes = thematic ? 3 + n % 3 : 0,
            CitationsFinal = new VerdictCounts
            {
                Total = total, Supported = v[0], PartiallySupported = v[1], NotSupported = v[2], InsufficientEvidence = v[3], Unverifiable = v[4],
            },
            Repaired = n % 5,
            SynthesisWords = 1800 + n * 97 % 900,
            DiscussionWords = 600 + n * 53 % 300,
            DurationMinutes = 14 + n % 6 * 2.5,
            LlmCalls = 50 + n * 7 % 40,
            LlmRetries = n % 4 == 0 ? 2 : 0,
            LlmFailures = n % 9 == 0 ? 1 : 0,
        }.WithTokens(n);
    }

    // The first five runs were made before tokens were counted (issue #414), so the page shows their dashes; the later ones
    // count their tokens by model, for the estimated cost (issue #504).
    private static RunMetrics WithTokens(this RunMetrics run, int n)
    {
        if (run.IsMultivocal)
            run.GreyPool = new PoolMetrics
            {
                Identified = run.Identified, Screened = run.Screened, Included = run.Included + 4, Kept = run.Included, Kappa = run.ScreeningKappa,
                KappaBasis = n % 2 == 0 ? "audit sample" : "all sources", Table7Scored = run.Included + 4, MeanTable7Points = 11.5,
            };
        if (n < 5) return run;
        run.InputTokens = 180_000 + n * 13_000 % 90_000;
        run.CachedInputTokens = run.InputTokens / 3;
        run.OutputTokens = 60_000 + n * 7_000 % 40_000;
        run.ReasoningTokens = run.OutputTokens * 4 / 5;
        run.Tokens = run.InputTokens.Value + run.OutputTokens.Value;
        run.LlmMinutes = Math.Round(run.DurationMinutes * 2.3, 1);
        // Most tokens go to the main model and a fifth of the input to the fast one; run 7 used a model with no price, so its cost is a dash.
        long main = run.InputTokens.Value * 4 / 5;
        run.CostByModel = new()
        {
            [n == 7 ? "mistral-medium-latest" : "mistral-large-latest"] = new StageCost { InputTokens = main, CachedInputTokens = run.CachedInputTokens, OutputTokens = run.OutputTokens - 5_000 },
            ["mistral-small-latest"] = new StageCost { InputTokens = run.InputTokens - main, CachedInputTokens = 0, OutputTokens = 5_000 },
        };
        return run;
    }
}

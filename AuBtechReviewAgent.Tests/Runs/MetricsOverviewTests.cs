using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The figures of the Metrics page, worked out in code (Runs/MetricsOverview.cs), redesign step 9.</summary>
public class MetricsOverviewTests
{
    private static RunMetrics Run(string id, string completed, string version, int supported, int partly, int not, int abstractOnly, int unverifiable,
        int included = 10, int cited = 8, double? kappa = null, double minutes = 10)
    {
        int total = supported + partly + not + abstractOnly + unverifiable;
        return new RunMetrics
        {
            RunId = id, CompletedUtc = completed, AppVersion = version, ConfigFingerprint = "fp-" + version, Model = "m",
            Included = included, StudiesCited = cited, ScreeningKappa = kappa, DurationMinutes = minutes,
            CitationsFinal = new VerdictCounts
            {
                Total = total, Supported = supported, PartiallySupported = partly, NotSupported = not, InsufficientEvidence = abstractOnly, Unverifiable = unverifiable,
            },
        };
    }

    [Fact]
    public void SharesArePooledOverCitationsAndStudiesNotAveragedOverRuns()
    {
        var overview = MetricsOverview.From(new[]
        {
            Run("bbbbbbbb2", "2026-11-02T10:00:00Z", "0.4.0", 90, 5, 5, 0, 0, included: 30, cited: 30),
            Run("aaaaaaaa1", "2026-11-01T10:00:00Z", "0.4.0", 0, 0, 10, 0, 0, included: 10, cited: 0),
        });

        // Sorted oldest first, whatever order the store gave.
        Assert.Equal("aaaaaaaa1", overview.Runs[0].RunId);
        Assert.Equal(110, overview.Pooled.Total);
        Assert.Equal(90, overview.Pooled.Supported);
        // 90 of 110 citations, not the mean of 90% and 0%.
        Assert.Equal("82%", MetricsOverview.Percent(overview.Pooled.SupportedShare));
        Assert.Equal(0.75, overview.CoverageShare, 3);
    }

    [Fact]
    public void GroupsByVersionPutTheLatestFirstAndAverageOnlyTheKappasThereAre()
    {
        var overview = MetricsOverview.From(new[]
        {
            Run("a", "2026-10-01T10:00:00Z", "0.3.0", 5, 0, 0, 0, 0, kappa: 0.6, minutes: 10),
            Run("b", "2026-10-05T10:00:00Z", "0.3.0", 5, 0, 0, 0, 0, kappa: null, minutes: 20),
            Run("c", "2026-11-01T10:00:00Z", "0.4.0", 3, 1, 0, 0, 0, kappa: 0.8, minutes: 30),
        });

        Assert.Equal(2, overview.Groups.Count);
        Assert.Equal("fp-0.4.0", overview.Groups[0].Fingerprint);
        var older = overview.Groups[1];
        Assert.Equal(2, older.Runs);
        Assert.Equal(0.6, older.ScreeningKappa!.Value, 3);
        Assert.Equal(15, older.MinutesPerRun, 3);
        Assert.Equal(0.7, overview.ScreeningKappa!.Value, 3);
        Assert.Equal(20, overview.MinutesPerRun, 3);
        Assert.Equal("–", MetricsOverview.Kappa(null));
    }

    [Fact]
    public void ABarStacksItsVerdictsFromTheBottomAndSkipsTheEmptyOnes()
    {
        var c = new VerdictCounts { Total = 10, Supported = 5, PartiallySupported = 0, NotSupported = 2, InsufficientEvidence = 2, Unverifiable = 1 };

        var segments = MetricsOverview.Segments(c);

        Assert.Equal(new[] { MetricsOverview.Verdict.Supported, MetricsOverview.Verdict.NotSupported, MetricsOverview.Verdict.AbstractOnly, MetricsOverview.Verdict.Unverifiable },
            segments.Select(s => s.Verdict));
        Assert.Equal(0, segments[0].Start, 6);
        Assert.Equal(0.5, segments[1].Start, 6);
        Assert.Equal(1.0, segments.Last().Start + segments.Last().Share, 6);
        Assert.Empty(MetricsOverview.Segments(new VerdictCounts()));
    }

    [Theory]
    [InlineData("195564c631be4be8a09dd7967c426c74", "195564c6")]
    [InlineData("abc", "abc")]
    public void TheShortRunIdIsItsFirstEightCharacters(string id, string expected) =>
        Assert.Equal(expected, MetricsOverview.ShortId(id));

    [Fact]
    public void TheCompletionTimeReadsAsDateAndMinute()
    {
        Assert.Equal("2026-11-20 14:03", MetricsOverview.Completed("2026-11-20T14:03:59Z"));
        Assert.Equal("not a date", MetricsOverview.Completed("not a date"));
    }

    [Fact]
    public void AnEmptyStoreGivesZerosNotErrors()
    {
        var overview = MetricsOverview.From(Array.Empty<RunMetrics>());

        Assert.Empty(overview.Runs);
        Assert.Empty(overview.Groups);
        Assert.Equal(0, overview.CoverageShare);
        Assert.Null(overview.ScreeningKappa);
        Assert.Equal(0, overview.MinutesPerRun);
    }
}

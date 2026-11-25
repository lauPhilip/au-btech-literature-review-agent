using System.Runtime.CompilerServices;
using AuBtechReviewAgent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The price table behind the Metrics page's estimated cost (issue #504) and the prices the repository sets.</summary>
public class MetricsPricesTests
{
    private static string AppSettingsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here)!, "..", "..", "AuBtechReviewAgent", "appsettings.json"));

    private static MetricsPrices Repository() =>
        (new ConfigurationBuilder().AddJsonFile(AppSettingsPath()).Build().GetSection("Metrics").Get<MetricsOptions>() ?? new MetricsOptions()).Prices;

    private static RunMetrics Run(params (string Model, long Input, long Cached, long Output)[] models) => new()
    {
        RunId = "run",
        CostByModel = models.ToDictionary(m => m.Model, m => new StageCost { InputTokens = m.Input, CachedInputTokens = m.Cached, OutputTokens = m.Output }),
    };

    [Fact]
    public void TheRepositorySetsMistralsListPricesInDollars()
    {
        var prices = Repository();
        Assert.True(prices.Enabled);
        Assert.Equal("USD", prices.Currency);
        var large = prices.Models["mistral-large-latest"];
        Assert.Equal((0.70, 0.07, 2.10), (large.InputPerMillion, large.CachedInputPerMillion, large.OutputPerMillion));
        var small = prices.Models["mistral-small-latest"];
        Assert.Equal((0.15, (double?)null, 0.60), (small.InputPerMillion, small.CachedInputPerMillion, small.OutputPerMillion));
    }

    [Fact]
    public void TheMultivocalRun50e9a8e3CostsAboutTwentyDollars()
    {
        // The token counts of llm-calls.json of the real run 50e9a8e3 (23 November 2026), all on mistral-large-latest.
        var run = Run(("mistral-large-latest", 6_989_940, 769_536, 7_620_775));
        Assert.Equal(20.4118, Repository().Cost(run));
        Assert.Equal("$20.41", Repository().Format(20.4118));
    }

    [Fact]
    public void AModelWithNoPriceIsNamedAndLeftOutOfTheMeanAndTotal()
    {
        var prices = Repository();
        var priced = Run(("mistral-large-latest", 1_000_000, 0, 0));
        var unpriced = Run(("mistral-large-latest", 1_000_000, 0, 0), ("mistral-medium-latest", 10, 0, 10), ("unused", 0, 0, 0));
        Assert.Null(prices.Cost(unpriced));
        Assert.Equal(new[] { "mistral-medium-latest" }, prices.Unpriced(unpriced));
        Assert.Empty(prices.Unpriced(priced));
        Assert.Empty(MetricsPrices.None.Unpriced(unpriced)); // off: nothing is named
        Assert.Equal(0.7, prices.MeanCost(new[] { priced, unpriced, new RunMetrics() }));
        Assert.Equal(1.4, prices.TotalCost(new[] { priced, priced, unpriced }));
        Assert.Null(prices.MeanCost(new[] { unpriced }));
        Assert.Null(MetricsPrices.None.TotalCost(new[] { priced }));
    }

    [Fact]
    public void CostsAreShownToTheCentWithTheCurrency()
    {
        Assert.Equal("$0.37", new MetricsPrices { Currency = "USD" }.Format(0.3714));
        Assert.Equal("€1,234.50", new MetricsPrices { Currency = "EUR" }.Format(1234.5));
        Assert.Equal("0.37 DKK", new MetricsPrices { Currency = "DKK" }.Format(0.37));
    }
}

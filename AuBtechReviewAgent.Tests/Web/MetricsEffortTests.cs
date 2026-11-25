using AuBtechReviewAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The Metrics page's effort table (issue #414), rendered with Blazor's HtmlRenderer.</summary>
public class MetricsEffortTests
{
    private static readonly MetricsPrices Prices = new()
    {
        Currency = "USD",
        Models = { ["mistral-large-latest"] = new ModelPrice { InputPerMillion = 0.70, CachedInputPerMillion = 0.07, OutputPerMillion = 2.10 } },
    };

    private static RunMetrics Run(string id, string completed, long? input, double minutes, string model = "mistral-large-latest") => new()
    {
        CostByModel = input is long i ? new() { [model] = new StageCost { InputTokens = i, CachedInputTokens = 0, OutputTokens = i / 2 } } : null,
        RunId = id + "000000000000000000000000",
        CompletedUtc = completed,
        AppVersion = "0.5.0+abcdef1234",
        SchemaVersion = 2,
        DurationMinutes = minutes,
        LlmCalls = 40,
        LlmRetries = 2,
        InputTokens = input,
        OutputTokens = input / 2,
        ReasoningTokens = input / 4,
        LlmMinutes = input == null ? null : minutes * 2,
    };

    [Fact]
    public async Task EachRunShowsItsEffortNewestFirstAndOldRunsShowADash()
    {
        var runs = new[] { Run("aaaaaaaa", "2026-11-01T10:00:00Z", null, 10), Run("bbbbbbbb", "2026-11-02T10:00:00Z", 1_200_000, 20) };

        string html = await RenderAsync(runs);

        Assert.Contains("<section id=\"effort\"", html);
        Assert.Contains(">Effort per run</h2>", html);
        Assert.True(html.IndexOf(">bbbbbbbb<", StringComparison.Ordinal) < html.IndexOf(">aaaaaaaa<", StringComparison.Ordinal));
        Assert.Contains(">1,200,000<", html);
        Assert.Contains(">600,000<", html);
        Assert.Contains(">40.0<", html); // call time of the newer run
        Assert.True(html.Contains(">–<") || html.Contains(">&#x2013;<"), "the older run counted no tokens, so it shows a dash");
        Assert.Contains(">15.0<", html); // the mean minutes over both runs
        Assert.DoesNotContain("$", html); // tokens only, no prices
        Assert.DoesNotContain("€", html);
        Assert.DoesNotContain("Estimated cost", html);
        Assert.DoesNotContain("today's prices", html);
    }

    [Fact]
    public async Task WithPricesEachRunShowsItsEstimatedCostWithTheMeanAndTotal()
    {
        // 1.2M input at $0.70 and 0.6M output at $2.10 per million: $0.84 + $1.26 = $2.10; 0.4M and 0.2M: $0.70.
        var runs = new[] { Run("aaaaaaaa", "2026-11-01T10:00:00Z", null, 10), Run("bbbbbbbb", "2026-11-02T10:00:00Z", 1_200_000, 20), Run("cccccccc", "2026-11-03T10:00:00Z", 400_000, 20) };

        string html = await RenderAsync(runs, Prices);

        Assert.Contains(">Estimated cost</th>", html);
        Assert.Contains("Estimated at today's prices from the token counts; not the price on the day of the run; reasoning tokens are billed as output.", html.Replace("&#x27;", "'"));
        Assert.Contains(">$2.10<", html);
        Assert.Contains(">$0.70<", html);
        Assert.Contains(">$1.40<", html); // the mean over the two runs with a cost
        Assert.Contains(">$2.80<", html); // the total
        Assert.Contains(">Total</th>", html);
        Assert.DoesNotContain("No price for", html);
    }

    [Fact]
    public async Task ARunWithAnUnpricedModelShowsADashAndANote()
    {
        var runs = new[] { Run("aaaaaaaa", "2026-11-01T10:00:00Z", 400_000, 10, "mistral-medium-latest"), Run("bbbbbbbb", "2026-11-02T10:00:00Z", 400_000, 20) };

        string html = await RenderAsync(runs, Prices);

        Assert.Contains("title=\"No price for mistral-medium-latest\"", html);
        Assert.Contains("no price in the table (mistral-medium-latest)", html);
        Assert.Equal(3, CountOf(html, ">$0.70<")); // the priced run, and the mean and the total over it alone
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public void TheSampleRunsShowBothOldAndNewRuns()
    {
        var runs = SampleMetrics.Runs().Where(r => !r.IsMultivocal).ToList();
        Assert.Contains(runs, r => r.InputTokens == null);
        Assert.Contains(runs, r => r.InputTokens > 0 && r.ReasoningTokens < r.OutputTokens);
    }

    private static async Task<string> RenderAsync(IReadOnlyList<RunMetrics> runs, MetricsPrices? prices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<MetricsEffort>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Runs"] = runs, ["Prices"] = prices ?? MetricsPrices.None }));
            return output.ToHtmlString();
        });
    }
}

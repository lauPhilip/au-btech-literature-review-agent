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
    private static RunMetrics Run(string id, string completed, long? input, double minutes) => new()
    {
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
    }

    [Fact]
    public void TheSampleRunsShowBothOldAndNewRuns()
    {
        var runs = SampleMetrics.Runs().Where(r => !r.IsMultivocal).ToList();
        Assert.Contains(runs, r => r.InputTokens == null);
        Assert.Contains(runs, r => r.InputTokens > 0 && r.ReasoningTokens < r.OutputTokens);
    }

    private static async Task<string> RenderAsync(IReadOnlyList<RunMetrics> runs)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<MetricsEffort>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Runs"] = runs }));
            return output.ToHtmlString();
        });
    }
}

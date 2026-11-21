using AuBtechReviewAgent.Components.Paper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The Metrics page after redesign step 9: the chart with one bar per run (Components/Paper/RunVerdictChart.razor,
/// rendered with Blazor's HtmlRenderer), the page itself (Components/Pages/Metrics.razor, read as source) and the
/// sample runs behind /design/metrics (Components/Pages/Design/SampleMetrics.cs).
/// </summary>
public class MetricsPageTests
{
    private static string? Source(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent", "Components" }.Concat(path).ToArray()));
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public async Task TheChartDrawsEveryRunWithItsShortIdAndATooltip()
    {
        var runs = MetricsOverview.From(SampleMetrics.Runs().Where(r => !r.IsMultivocal)).Runs;

        string html = await RenderChartAsync(runs, grey: false);

        Assert.Equal(runs.Count, Count(html, "<g>"));
        foreach (var run in runs)
        {
            Assert.Contains($">{MetricsOverview.ShortId(run.RunId)}</text>", html);
            Assert.Contains($"<title>Run {MetricsOverview.ShortId(run.RunId)} ", html);
        }
        Assert.Contains(">Run ID</text>", html);
        foreach (string tick in new[] { ">0%<", ">25%<", ">50%<", ">75%<", ">100%<" })
            Assert.Contains(tick, html);
        // Oldest on the left: the first label belongs to the oldest run.
        Assert.True(html.IndexOf(MetricsOverview.ShortId(runs[0].RunId), StringComparison.Ordinal) < html.IndexOf(MetricsOverview.ShortId(runs[^1].RunId), StringComparison.Ordinal));
        Assert.Contains("rotate(-90 ", html);
        Assert.Contains("font-mono", html);
        Assert.Contains("Swipe the chart to see all runs.", html);
        Assert.Contains("role=\"img\" aria-label=\"Stacked bars of the share of each citation verdict in 14 runs", html);
        Assert.Contains("tabindex=\"0\" role=\"region\"", html);
    }

    [Fact]
    public async Task TheChartIsNeverNarrowerThan560Pixels()
    {
        var one = MetricsOverview.From(SampleMetrics.Runs().Take(1)).Runs;

        string html = await RenderChartAsync(one, grey: false);

        Assert.Contains("<svg width=\"560\"", html);
    }

    [Fact]
    public async Task TheGreyLiteratureChartUsesItsOwnWordForTheFifthVerdict()
    {
        var runs = MetricsOverview.From(SampleMetrics.Runs().Where(r => r.IsMultivocal)).Runs;

        string html = await RenderChartAsync(runs, grey: true);

        Assert.Contains("Not judged from the kept text", html);
        Assert.Contains("not judged from the kept text", html);
        Assert.DoesNotContain("Abstract only", html);
    }

    [Fact]
    public void ThePageHasTheFiguresTheJumpLinksAndTheThreeTablesWithTotals()
    {
        string? page = Source("Pages", "Metrics.razor");
        if (page == null) return; // not run from the repository (packaged tests)

        Assert.Contains("How well the citations hold up", page);
        foreach (string id in new[] { "chart", "shares", "run-data", "by-version" })
        {
            Assert.Contains($"(\"{id}\", ", page);
            Assert.Contains($"<section id=\"{id}\"", page);
        }
        Assert.Contains("data-jump-to=\"@sectionId\"", page);
        Assert.Contains("<RunVerdictChart ", page);
        Assert.Equal(3, Count(page, "<tfoot>"));
        Assert.Contains("sticky top-0", page);
        // The ?kind= tabs stay, and the old per-run detail is gone (Lau chose to leave it out for now).
        Assert.Contains("/metrics?kind=", page);
        Assert.DoesNotContain("Where the citations", page);
        Assert.DoesNotContain("Cost by stage", page);
    }

    [Fact]
    public void TheSampleRunsCoverBothKindsEveryVerdictAndHaveDistinctShortIds()
    {
        var runs = SampleMetrics.Runs();

        Assert.Equal(14, runs.Count(r => !r.IsMultivocal));
        Assert.Equal(4, runs.Count(r => r.IsMultivocal));
        Assert.Equal(runs.Count, runs.Select(r => MetricsOverview.ShortId(r.RunId)).Distinct().Count());
        var pooled = MetricsOverview.Pool(runs.Select(r => r.CitationsFinal));
        Assert.True(pooled.Supported > 0 && pooled.PartiallySupported > 0 && pooled.NotSupported > 0 && pooled.InsufficientEvidence > 0 && pooled.Unverifiable > 0);
        Assert.All(runs, r => Assert.Equal(r.CitationsFinal.Total,
            r.CitationsFinal.Supported + r.CitationsFinal.PartiallySupported + r.CitationsFinal.NotSupported + r.CitationsFinal.InsufficientEvidence + r.CitationsFinal.Unverifiable));
        Assert.True(MetricsOverview.From(runs.Where(r => !r.IsMultivocal)).Groups.Count > 1);
    }

    private static async Task<string> RenderChartAsync(IReadOnlyList<RunMetrics> runs, bool grey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<RunVerdictChart>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Runs"] = runs, ["Grey"] = grey,
            }));
            return output.ToHtmlString();
        });
    }
}

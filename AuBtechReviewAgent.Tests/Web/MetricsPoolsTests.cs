using AuBtechReviewAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The grey literature tab's pool table (issue #415) and the downloads box (issue #416) of the Metrics page.</summary>
public class MetricsPoolsTests
{
    [Fact]
    public async Task EachPoolIsShownApartAndAGreyOnlyRunShowsDashesForTheAcademicPool()
    {
        var runs = new[]
        {
            new RunMetrics { RunId = "aaaaaaaa0000", CompletedUtc = "2026-11-20T10:00:00Z", ReviewType = "multivocal",
                GreyPool = new PoolMetrics { Identified = 40, Included = 12, Kept = 9, Kappa = 0.55, KappaBasis = "all sources" } },
            new RunMetrics { RunId = "bbbbbbbb0000", CompletedUtc = "2026-11-24T10:00:00Z", ReviewType = "multivocal",
                GreyPool = new PoolMetrics { Identified = 31, Included = 8, Kept = 6, Kappa = 0.7, KappaBasis = "audit sample" },
                FormalPool = new PoolMetrics { Identified = 57, Included = 11, Kept = 11, Kappa = 0.8, KappaBasis = "audit sample" } },
            new RunMetrics { RunId = "cccccccc0000", CompletedUtc = "2026-11-01T10:00:00Z", ReviewType = "multivocal" }, // recorded before the pools
        };

        string html = await RenderAsync<MetricsPools>(new Dictionary<string, object?> { ["Runs"] = runs });

        Assert.Contains("<section id=\"pools\"", html);
        Assert.True(html.IndexOf(">bbbbbbbb<", StringComparison.Ordinal) < html.IndexOf(">aaaaaaaa<", StringComparison.Ordinal)); // newest first
        Assert.DoesNotContain(">cccccccc<", html);
        Assert.Contains(">57<", html);
        Assert.Contains(">0.80<", html);
        Assert.Contains(">audit sample<", html);
        Assert.Contains(">all sources<", html);
    }

    [Fact]
    public async Task SystematicRunsShowNoPoolTable()
    {
        string html = await RenderAsync<MetricsPools>(new Dictionary<string, object?> { ["Runs"] = new[] { new RunMetrics { RunId = "s" } } });
        Assert.DoesNotContain("<section", html);
    }

    [Fact]
    public async Task TheDownloadsLinkTheThreeFilesAndTheDictionary()
    {
        string html = await RenderAsync<MetricsFiles>(new Dictionary<string, object?>());

        foreach (string file in new[] { MetricsDownloads.RunsCsv, MetricsDownloads.RunsJson, MetricsDownloads.StagesCsv })
            Assert.Contains($"href=\"{file}\" download", html);
        Assert.Contains("/blob/master/docs/metrics-dictionary.md", html);
        Assert.Contains("<section id=\"files\"", html);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
}

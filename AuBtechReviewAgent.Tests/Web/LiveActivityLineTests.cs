using AuBtechReviewAgent;
using AuBtechReviewAgent.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The live line rendered to HTML (HtmlRenderer): text from sources is encoded, never markup, and only the kind of work is announced.</summary>
public class LiveActivityLineTests
{
    private static async Task<string> RenderAsync(LiveLine? line, bool announce)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Line"] = line, ["Announce"] = announce });
            var output = await renderer.RenderComponentAsync<LiveActivityLine>(parameters);
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task ATitleWithMarkupIsShownAsText()
    {
        var stats = new ReviewStats { ProgressStep = RunProgress.FullText, ProgressSubject = LiveActivity.ShortCite(null, "", "<img src=x onerror=alert(1)>") };
        string html = await RenderAsync(LiveActivity.ForSystematic(stats, "Screening", 0), false);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("Reading the full text of “<img src=x onerror=alert(1)>”…", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task TheLiveRegionSaysTheKindOfWorkNotTheCount()
    {
        var stats = new ReviewStats { ProgressStep = RunProgress.Screening, ProgressDetail = "119 of 431 records screened", Included = 42 };
        string html = System.Net.WebUtility.HtmlDecode(await RenderAsync(LiveActivity.ForSystematic(stats, "Screening", 0), true));
        Assert.Contains("Screening record 120 of 431…", html);
        Assert.Contains("42 included", html);
        Assert.Contains("aria-live=\"polite\">Now screening the records.</p>", html);
        Assert.Contains("class=\"ui-live-dot\"", html);
    }

    [Fact]
    public async Task WithoutALineNothingIsShownAndAWaitDoesNotPulse()
    {
        Assert.DoesNotContain("ui-live", await RenderAsync(null, false));
        string queued = await RenderAsync(LiveActivity.ForSystematic(new ReviewStats(), PrismaReviewEngine.StageQueued, 2), false);
        Assert.Contains("ui-live-dot-wait", queued);
        Assert.DoesNotContain("aria-live", queued);
    }
}

using AuBtechReviewAgent.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The finished run's note on included papers whose full text reads like instructions to the model (#366).</summary>
public class FullTextFlagsNoteTests
{
    [Fact]
    public async Task EachFlaggedPaperIsListedWithItsPhrase()
    {
        var flags = new List<SourceTextFlags>
        {
            new("p1", "Agents that loop", new List<SourceFlag> { new("ignore-instructions", "…ignore all previous instructions…") }),
        };

        string html = await RenderAsync(flags);

        Assert.Contains("1 included paper has text that reads like instructions to the model", html);
        Assert.Contains("Agents that loop", html);
        Assert.Contains("asks to ignore instructions or rules", html);
        Assert.Contains("changed no decision", html);
    }

    [Fact]
    public async Task APaperThatOnlyNamesPromptsIsNotListed()
    {
        var flags = new List<SourceTextFlags>
        {
            new("p1", "Context engineering", new List<SourceFlag> { new("mentions-system-prompt", "…the system prompt holds the role…") }),
            new("p2", "Agents that loop", new List<SourceFlag> { new("mentions-system-prompt", "…system prompt…"), new("role-change", "…you are now a judge…") }),
        };

        string html = await RenderAsync(flags);

        Assert.Contains("1 included paper has text that reads like instructions to the model", html);
        Assert.DoesNotContain("Context engineering", html);
        Assert.DoesNotContain("mentions the system or developer prompt", html);
        Assert.Contains("tries to give the model new instructions", html);
    }

    [Fact]
    public async Task PapersWithHiddenTextTakenOutAreListedApart()
    {
        var flags = new List<SourceTextFlags>
        {
            new("p1", "Agents that loop", new List<SourceFlag>(), new HiddenTextRemoved(3, 0)),
        };

        string html = await RenderAsync(flags);

        Assert.DoesNotContain("reads like instructions", html);
        Assert.Contains("Hidden text was taken out of 1 included paper", html);
        Assert.Contains("3 invisible characters removed", html);
    }

    [Fact]
    public async Task NothingShowsWithoutFlags()
    {
        Assert.Equal("", (await RenderAsync(null)).Trim());
        Assert.Equal("", (await RenderAsync(new List<SourceTextFlags>())).Trim());
    }

    [Fact]
    public void TheFinishedRunViewShowsTheNote()
    {
        string? page = PageSource.Read("Home");
        if (page == null) return;
        Assert.Contains("<FullTextFlagsNote Flags=\"runInfo?.FullTextFlags\" />", page);
    }

    private static async Task<string> RenderAsync(List<SourceTextFlags>? flags)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<FullTextFlagsNote>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Flags"] = flags }))).ToHtmlString());
    }
}

using AuBtechReviewAgent.Components.Landing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The landing page of the redesign: one screen with the hero and the looping citation example (CitationLoop),
/// rendered to HTML with Blazor's HtmlRenderer as a browser would receive it from the server.
/// </summary>
public class LandingTests
{
    [Fact]
    public async Task TheExampleChecksThreeCitationsWithEachVerdict()
    {
        string html = await RenderAsync<CitationLoop>();

        Assert.Equal(new[] { 5, 4, 10 }, CitationLoop.Citations.Select(c => c.Number));
        Assert.Equal(new[] { "Supported", "Partly supported", "Not supported" }, CitationLoop.Citations.Select(c => c.Verdict));
        foreach (var citation in CitationLoop.Citations)
        {
            Assert.Contains($"Checking citation {citation.Number} against the paper", html);
            Assert.Contains(citation.Verdict, html);
            // Each verdict shows a quote from the paper, or, where nothing backs the claim, the reason.
            Assert.True(citation.Quote.Length > 0 ^ citation.Reason.Length > 0, $"[{citation.Number}] needs a quote or a reason");
            Assert.Contains(citation.Quote.Length > 0 ? citation.Quote : citation.Reason, html);
        }
        Assert.Empty(CitationLoop.Citations.Single(c => c.Verdict == "Not supported").Quote);
    }

    [Fact]
    public async Task TheExampleCanBePausedAndIsReadOnceByScreenReaders()
    {
        string html = await RenderAsync<CitationLoop>();

        Assert.Contains("<button type=\"button\" data-loop-toggle", html);
        Assert.Contains("Pause the example", html);
        Assert.Contains("Play the example", html);
        Assert.Contains("h-11 w-11", html);
        // Hidden under 640 px, so the landing page fits a phone's screen.
        Assert.Contains("max-sm:hidden", html);
        // The moving card is hidden from screen readers; the caption names the three verdicts once.
        Assert.Contains("aria-hidden=\"true\"", html);
        Assert.Contains("<figcaption", html);
    }

    [Fact]
    public void TheLoopsTimingAndPauseAreInTheStylesAndTheScript()
    {
        string? app = AppRoot();
        if (app == null) return; // not run from the repository
        string css = File.ReadAllText(Path.Join(app, "Styles", "tailwind.input.css"));
        foreach (var part in new[] { "@keyframes tai-loop-check", "@keyframes tai-loop-verdict", "@keyframes tai-loop-cite",
                     "animation-duration: 12.6s", ".tai-loop-1 {", ".tai-loop-2 {", ".tai-loop-3 {",
                     ".tai-loop[data-state=\"paused\"] .tai-loop-anim", "prefers-reduced-motion" })
            Assert.Contains(part, css);

        string js = File.ReadAllText(Path.Join(app, "wwwroot", "js", "citation-loop.js"));
        Assert.Contains("data-loop-toggle", js);
        Assert.Contains("data-state", js);
        string page = File.ReadAllText(Path.Join(app, "Components", "App.razor"));
        Assert.Contains("js/citation-loop.js", page);
    }

    [Fact]
    public void TheLandingPageIsOneScreenWithTheExample()
    {
        string? app = AppRoot();
        if (app == null) return; // not run from the repository
        string page = File.ReadAllText(Path.Join(app, "Components", "Pages", "Landing.razor"));

        Assert.Contains("@layout LandingLayout", page);
        Assert.Contains("<CitationLoop />", page);
        Assert.Contains("Literature reviews where every citation is checked", page);
        Assert.Contains("href=\"/review\"", page);
        Assert.Contains("href=\"/about\"", page);
        // No statistics row and no fixed colours: the page takes the dark theme.
        Assert.DoesNotContain("MetricsStore", page);
        Assert.DoesNotMatch(@"\b(bg-white|text-gray-\d+|bg-brand|text-brand)\b", page);
    }

    private static string? AppRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : Path.Join(root, "AuBtechReviewAgent");
    }

    private static async Task<string> RenderAsync<TComponent>() where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
            return output.ToHtmlString();
        });
    }
}

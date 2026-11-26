using AuBtechReviewAgent.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Where a visitor can go from the header and the main path: Example reports is the fourth place of the header
/// (issue #638), an empty Review output page invites instead of leaving a dead end (#640), and the landing page's
/// "Start a review" goes through the chooser at /start (#641).
/// </summary>
public class NavigationPlacesTests
{
    private const string RunId = "195564c6-31be-4be8-a09d-d7967c426c74";

    [Theory]
    [InlineData("review")]
    [InlineData("spec-matrix")]
    [InlineData("review/" + RunId)]
    [InlineData("about")]
    public async Task TheHeaderHasFourPlacesInOrderOnEveryAppPage(string path)
    {
        string html = await RenderHeaderAsync(path);
        string wide = html[html.IndexOf("<ul class=\"hidden", StringComparison.Ordinal)..html.IndexOf("<details", StringComparison.Ordinal)];

        var labels = new[] { "New review", "Review output", "Example reports", "Metrics" };
        int last = -1;
        foreach (var label in labels)
        {
            int at = wide.IndexOf(">" + label + "</a>", StringComparison.Ordinal);
            Assert.True(at > last, $"{label} is missing or out of order on /{path}");
            last = at;
        }
        Assert.Contains("href=\"/gallery\"", wide);
        // The trust pages stay in the footer.
        Assert.DoesNotContain("/model-card", html);
        Assert.DoesNotContain("href=\"/about\"", html);
    }

    [Fact]
    public async Task ExampleReportsLeadsToTheGalleryWhateverRunIsOpenAndIsMarkedThere()
    {
        string onRun = await RenderHeaderAsync("spec-matrix/" + RunId);
        Assert.Contains($"href=\"/review/{RunId}\"", onRun);
        Assert.Contains("href=\"/gallery\"", onRun);

        string inGallery = await RenderHeaderAsync("gallery");
        Assert.Contains("href=\"/gallery\" aria-current=\"page\"", inGallery);
        Assert.Equal("gallery", SiteHeader.Locate("gallery/some-report").Active);
        Assert.Equal("output", SiteHeader.Locate("spec-matrix/" + RunId).Active);
    }

    [Fact]
    public async Task TheLandingHeaderStillHasNoPlaces()
    {
        string landing = await RenderHeaderAsync("", new() { ["ShowNav"] = false, ["ShowThemeToggle"] = true });
        Assert.DoesNotContain("Example reports", landing);
    }

    [Fact]
    public void TheEmptyOutputPageExplainsItself_LinksToExamplesAndStartsAReview()
    {
        string? page = PageSource.Read("SpecMatrix");
        if (page == null) return; // not run from the repository
        string empty = page[page.IndexOf("@if (!HasReport)", StringComparison.Ordinal)..page.IndexOf("string runHref", StringComparison.Ordinal)];

        Assert.Contains("No report yet", empty);
        Assert.Contains("This page shows the finished report of your review", empty);
        Assert.Contains("href=\"/gallery\"", empty);
        Assert.Contains("See example reports", empty);
        Assert.Contains("href=\"/start\" class=\"ui-btn-primary", empty);
        Assert.Contains("Start a review", empty);
    }

    private static string? AppRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : Path.Join(root, "AuBtechReviewAgent");
    }

    private static async Task<string> RenderHeaderAsync(string path, Dictionary<string, object?>? parameters = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new FixedNavigation(path));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<SiteHeader>(ParameterView.FromDictionary(parameters ?? new()))).ToHtmlString());
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string path) => Initialize("https://example.org/", "https://example.org/" + path);
    }
}

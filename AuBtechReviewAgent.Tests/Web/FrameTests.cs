using AuBtechReviewAgent.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The frame of the redesign: the header, footer, theme toggle, mark and the two layouts, rendered to HTML with
/// Blazor's HtmlRenderer as a browser would receive them from the server.
/// </summary>
public class FrameTests
{
    private const string RunId = "195564c6-31be-4be8-a09d-d7967c426c74";

    [Theory]
    [InlineData("", "overview", "/review", "/spec-matrix")]
    [InlineData("review", "dashboard", "/review", "/spec-matrix")]
    [InlineData("review/" + RunId, "dashboard", "/review/" + RunId, "/spec-matrix/" + RunId)]
    [InlineData("spec-matrix/" + RunId + "?tab=paper#s2", "output", "/review/" + RunId, "/spec-matrix/" + RunId)]
    [InlineData("spec-matrix", "output", "/review", "/spec-matrix")]
    [InlineData("mlr-paper/" + RunId, "dashboard", "/mlr/" + RunId, "/spec-matrix")]
    [InlineData("start", "dashboard", "/review", "/spec-matrix")]
    [InlineData("metrics?kind=grey", "metrics", "/review", "/spec-matrix")]
    [InlineData("about", "", "/review", "/spec-matrix")]
    [InlineData("review/not-a-run", "dashboard", "/review", "/spec-matrix")]
    public void TheHeaderReadsTheCurrentPlaceFromTheAddress(string path, string active, string dashboard, string output)
    {
        var place = SiteHeader.Locate(path);

        Assert.Equal(active, place.Active);
        Assert.Equal(dashboard, place.DashboardHref);
        Assert.Equal(output, place.OutputHref);
    }

    [Fact]
    public async Task TheHeaderMarksTheCurrentPlaceAndLinksToTheRun()
    {
        string html = await RenderAsync<SiteHeader>("spec-matrix/" + RunId);

        Assert.Contains("href=\"#main\"", html);
        Assert.Contains("aria-label=\"Main\"", html);
        Assert.Contains($"href=\"/review/{RunId}\"", html);
        // Both the wide list and the phone menu mark Review output, and nothing else.
        Assert.Equal(2, Count(html, "aria-current=\"page\""));
        Assert.Contains("<details", html);
        Assert.DoesNotContain("data-theme-toggle", html);
    }

    [Fact]
    public async Task ThePageCanOverrideTheHeaderAndTheLandingHeaderHasNoPlaces()
    {
        string html = await RenderAsync<SiteHeader>("review", new() { ["Active"] = "metrics", ["DashboardHref"] = "/mlr/x" });
        Assert.Contains("href=\"/mlr/x\"", html);
        Assert.Contains("href=\"/metrics\" aria-current=\"page\"", html);

        string landing = await RenderAsync<SiteHeader>("", new() { ["ShowNav"] = false, ["ShowThemeToggle"] = true });
        Assert.DoesNotContain("<nav", landing);
        // The toggle appears only once ThemeToggle.Offered is on, while the pages use fixed light colours.
        Assert.Equal(ThemeToggle.Offered, landing.Contains("data-theme-toggle", StringComparison.Ordinal));
        Assert.Contains("href=\"/\" aria-current=\"page\"", landing);
    }

    [Fact]
    public async Task TheThemeToggleIsARealButtonWithANameForEachTheme()
    {
        string html = await RenderAsync<ThemeToggle>("");

        Assert.Contains("<button type=\"button\" data-theme-toggle", html);
        Assert.Contains("Switch to dark theme", html);
        Assert.Contains("Switch to light theme", html);
    }

    [Fact]
    public async Task TheMarkShowsBothBareMarksAndTheirFilesExist()
    {
        string html = await RenderAsync<BrandMark>("", new() { ["Size"] = 96 });

        Assert.Contains("brand/traceableai-mark-navy.svg", html);
        Assert.Contains("brand/traceableai-mark-light.svg", html);
        Assert.Contains("width: 96px", html);
        string? web = WebRoot();
        if (web == null) return;
        Assert.True(File.Exists(Path.Join(web, "brand", "traceableai-mark-navy.svg")));
        Assert.True(File.Exists(Path.Join(web, "brand", "traceableai-mark-light.svg")));
    }

    [Fact]
    public async Task TheFooterHasTheTrustPagesOrTheLandingLinks()
    {
        string app = await RenderAsync<SiteFooter>("review");
        foreach (var href in new[] { "/about", "/model-card", "/privacy", "/verify", "/glossary", SiteSeo.RepositoryUrl })
            Assert.Contains($"href=\"{href}\"", app);

        string landing = await RenderAsync<SiteFooter>("", new() { ["Landing"] = true });
        Assert.Contains("OSSYM 2026", landing);
        Assert.Contains("href=\"/metrics\"", landing);
        Assert.DoesNotContain("href=\"/privacy\"", landing);
    }

    [Fact]
    public async Task TheLayoutsWrapThePageInOneMainBetweenHeaderAndFooter()
    {
        RenderFragment body = b => b.AddContent(0, "PAGE CONTENT");
        foreach (var html in new[]
        {
            await RenderAsync<AppLayout>("metrics", new() { ["Body"] = body }),
            await RenderAsync<LandingLayout>("", new() { ["Body"] = body }),
        })
        {
            Assert.Equal(1, Count(html, "<main id=\"main\""));
            Assert.Contains("h-dvh", html);
            int header = html.IndexOf("<header", StringComparison.Ordinal);
            int content = html.IndexOf("PAGE CONTENT", StringComparison.Ordinal);
            int footer = html.IndexOf("<footer", StringComparison.Ordinal);
            Assert.True(header >= 0 && header < content && content < footer);
            Assert.Equal(ThemeToggle.Offered, html.Contains("data-theme-toggle", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheThemeScriptIsAFileLoadedInTheHeadBeforeTheStyles()
    {
        string? web = WebRoot();
        if (web == null) return; // not run from the repository
        string app = File.ReadAllText(Path.Join(web, "..", "Components", "App.razor"));
        int head = app.IndexOf("</head>", StringComparison.Ordinal);
        int script = app.IndexOf("js/theme.js", StringComparison.Ordinal);
        int styles = app.IndexOf("rel=\"stylesheet\"", StringComparison.Ordinal);
        Assert.True(script > 0 && script < styles && styles < head, "theme.js must load in <head> before the stylesheets");
        Assert.Contains("data-follow-system=", app);
        // The Content-Security-Policy allows no inline scripts.
        Assert.DoesNotMatch("<script>(?!</script>)", app);

        string js = File.ReadAllText(Path.Join(web, "js", "theme.js"));
        Assert.Contains("data-theme-toggle", js);
        Assert.Contains("localStorage", js);
        Assert.Contains("prefers-color-scheme", js);
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string? WebRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : Path.Join(root, "AuBtechReviewAgent", "wwwroot");
    }

    private static async Task<string> RenderAsync<TComponent>(string path, Dictionary<string, object?>? parameters = null) where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new FixedNavigation(path));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters ?? new()));
            return output.ToHtmlString();
        });
    }

    /// <summary>A NavigationManager that is already at the given address, as on a page rendered by the server.</summary>
    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string path) => Initialize("https://example.org/", "https://example.org/" + path);
    }
}

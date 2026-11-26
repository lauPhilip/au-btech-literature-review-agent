using AuBtechReviewAgent;
using AuBtechReviewAgent.Components.Paper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report's front page on screen and on paper (Components/Paper/ArticleFront.razor, wiki page 06): the masthead,
/// the banner, the byline, the Article info box with a label on every group, the abstract with bold headings, and the
/// buttons that work on screen and become links on paper. Every value comes from the run in code.
/// </summary>
public class ArticleFrontTests
{
    private static readonly Guid Run = Guid.Parse("596541db-400c-4b99-9a8a-a5dc0ae9a9d3");

    private static ArticleInfo Info(string @abstract = "Background: agents act alone. Methods: four databases were searched.\n\nResults: 41 studies. Limitations: one model. Interpretation: rarely tested.") => new()
    {
        RunId = Run, Title = "Which safety mechanisms do agentic AI frameworks use?", Abstract = @abstract,
        Kind = "Systematic review", Series = "Systematic literature review", Guideline = "PRISMA 2020",
        ProtocolWrittenUtc = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc), SearchedUtc = new DateTime(2026, 11, 25, 8, 5, 0, DateTimeKind.Utc),
        CompletedUtc = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc), AppVersion = "0.6.0+40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961",
        Models = new[] { "mistral-large-latest" }, Found = 1284, Screened = 1100, Included = 41, ReadInFull = 32,
        Citations = new VerdictTally(118, 2, 1, 0, 0, Removed: 4), Keywords = new[] { "agentic AI", "fault tolerance" },
        ReportPath = $"spec-matrix/{Run}", HasProtocol = true,
    };

    [Fact]
    public void AStructuredAbstractKeepsItsHeadingsAndNoEmptyParagraph()
    {
        var parts = AbstractParts.Split(Info().Abstract);

        Assert.Equal(new[] { "Background", "Methods", "Results", "Limitations", "Interpretation" }, parts.Select(p => p.Heading));
        Assert.Equal("four databases were searched.", parts[1].Text);
        Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p.Text)));

        var plain = AbstractParts.Split("A plain abstract.\n\n\n  Second paragraph.  \n");
        Assert.Equal(new[] { "A plain abstract.", "Second paragraph." }, plain.Select(p => p.Text));
        Assert.All(plain, p => Assert.Null(p.Heading));
        // One heading word in the running text does not make an abstract structured.
        Assert.Single(AbstractParts.Split("The results: mixed. Nothing else."));
        Assert.Empty(AbstractParts.Split("  "));
    }

    [Fact]
    public void EveryGroupOfArticleInfoHasALabelAndOnlyDataTheRunHas()
    {
        var groups = Info().InfoGroups();

        Assert.Equal(new[] { "Article history", "Records", "Checks", "Keywords", "Registration" }, groups.Select(g => g.Label));
        Assert.Equal(new[] { "Protocol fixed 25 Nov 2026", "Search run 25 Nov 2026", "Report written 25 Nov 2026" }, groups[0].Lines);
        Assert.Equal(new[] { "1,284 identified", "1,100 screened", "41 included", "32 of 41 read in full" }, groups[1].Lines);
        // Supported of checked, without the removed: the same numbers as the report's box and the run page.
        Assert.Equal(new[] { "Citations 118 of 121 supported" }, groups[2].Lines);
        Assert.Equal(new[] { "Not registered" }, groups[4].Lines);

        var bare = new ArticleInfo { RunId = Run, Found = 3, Included = 0 }.InfoGroups();
        Assert.Equal(new[] { "Records", "Registration" }, bare.Select(g => g.Label));
        Assert.Equal(new[] { "3 identified", "0 included" }, bare[0].Lines);
    }

    [Fact]
    public async Task TheFrontPageShowsTheMastheadBylineAndBoldHeadings()
    {
        string html = await RenderAsync(Info());

        Assert.Contains("<p class=\"af-wordmark\">TraceableAI</p>", html);
        Assert.Contains("Systematic literature review · PRISMA 2020", html);
        Assert.Contains("Generated 25 November 2026", html);
        Assert.Contains("Run 596541db · v0.6.0", html);
        Assert.Contains(ArticleInfo.Disclosure, html);
        Assert.Contains("TraceableAI – artificial researcher<sup>*</sup>", html);
        Assert.Contains("Software, not an author. The person who ran this review is responsible for its use. Model: mistral-large-latest. Cite as software: TraceableAI v0.6.0.", html);
        Assert.Contains("<p><strong>Background.</strong> agents act alone.</p>", html);
        Assert.Contains("<p><strong>Interpretation.</strong> rarely tested.</p>", html);
        Assert.Contains("<dt>Registration</dt>", html);
        // No university, department or funder on a user's paper, and no journal's name.
        foreach (string absent in new[] { "Aarhus", "Department", "BTECH", "funded", "Journal" })
            Assert.DoesNotContain(absent, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rounded", html.Replace("ui-btn", ""));
    }

    [Fact]
    public async Task EveryButtonWorksOnScreenAndIsALinkOnPaper()
    {
        string html = await RenderAsync(Info());
        string site = "https://example.org/";

        Assert.Contains("data-print", html); // the PDF button, handled by wwwroot/js/review-output.js
        Assert.Contains($"data-copy=\"cite-text-596541db\" data-copy-label=\"Cite\"", html);
        Assert.Contains($"<span id=\"cite-text-596541db\">TraceableAI. (2026). Which safety mechanisms do agentic AI frameworks use? [Systematic review, run 596541db; not peer reviewed]. Version 0.6.0. {site}spec-matrix/{Run}</span>", html);
        Assert.Contains($"data-copy-status=\"cite-text-596541db\"", html);
        Assert.Contains($"href=\"{site}api/workspace/{Run}/archive\" class=\"ui-btn-secondary", html);
        Assert.Contains($"href=\"{site}api/workspace/{Run}/screened.ris\" class=\"ui-btn-secondary", html);
        // On paper: the buttons are hidden and the same places are written out as links.
        Assert.Contains("class=\"af-actions print:hidden\"", html);
        foreach (string path in new[] { $"spec-matrix/{Run}", $"api/workspace/{Run}/archive", $"api/workspace/{Run}/screened.ris", $"api/workspace/{Run}/protocol.md" })
            Assert.Contains($"<a href=\"{site}{path}\">{site}{path}</a>", html);

        string script = Asset("js", "review-output.js");
        if (script.Length == 0) return; // packaged tests
        Assert.Contains("closest('[data-print]')", script);
        Assert.Contains("data-copy-label", script);
    }

    [Fact]
    public async Task TheAbstractIsTheContentsTargetOnlyWhenThePageAsks()
    {
        string withId = await RenderAsync(Info(), ReportOutline.AbstractId);
        string without = await RenderAsync(Info());

        Assert.Contains($"id=\"{ReportOutline.AbstractId}\" data-report-section=\"\"", withId);
        Assert.DoesNotContain("data-report-section", without);
    }

    [Fact]
    public void TheReportPageIsLaidOutAsPagesWithAFooterOnEach()
    {
        string? page = ReportPageSource.Read();
        if (page == null) return; // packaged tests
        Assert.Contains("<div data-page-overlay aria-hidden=\"true\"></div>", page);
        Assert.Contains("<article data-paged-sheet=", page);
        Assert.Contains("<ArticleFront Info=\"articleInfo\" OnScreen=\"true\"", page);

        string script = Asset("js", "paged-preview.js");
        Assert.Contains("'AI-generated · machine-marked'", script);
        Assert.Contains("(n + 1) + ' of ' + pages", script);
        Assert.Contains("beforeprint", script); // paper has its own pages
        Assert.Contains("MIN_WIDTH = 768", script); // a phone keeps one continuous sheet
        Assert.DoesNotContain("innerHTML", script);
        Assert.Contains("js/paged-preview.js", Asset("..", "Components", "App.razor"));

        string css = Asset("css", "report-paper.css");
        Assert.Contains("[data-theme=\"dark\"] .paged-page", css); // the sheets follow the theme on screen
        Assert.Contains("[data-paged-sheet] { background: #fff !important;", css); // and are white on paper
        Assert.DoesNotContain("border-radius", css);
    }

    private static string Asset(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return "";
        return File.ReadAllText(Path.GetFullPath(Path.Join(new[] { root, "AuBtechReviewAgent", "wwwroot" }.Concat(path).ToArray())));
    }

    private static async Task<string> RenderAsync(ArticleInfo info, string? abstractId = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new FixedNavigation());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ArticleFront>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Info"] = info, ["OnScreen"] = true, ["AbstractId"] = abstractId,
            }));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation() => Initialize("https://example.org/", "https://example.org/spec-matrix");
    }
}

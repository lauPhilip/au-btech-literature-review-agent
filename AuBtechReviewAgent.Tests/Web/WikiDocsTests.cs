using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The developer wiki as the site serves it under /docs (issue #613): built from docs/wiki, links kept safe.</summary>
public class WikiDocsTests
{
    private static readonly string[] Files = { "README.md", "01-system-overview.md", "06-web-app-and-operations.md" };

    [Fact]
    public void EveryWikiFileIsBuiltIntoTheAppWithTheFrontPageFirst()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotEmpty(WikiDocs.Pages);
        Assert.Equal("", WikiDocs.Pages[0].Slug);
        Assert.Equal("TraceableAI developer wiki", WikiDocs.Pages[0].Title);
        if (root == null) return; // not run from the repository (packaged tests)

        var onDisk = Directory.GetFiles(Path.Join(root, "docs", "wiki"), "*.md").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(onDisk, WikiDocs.Pages.Select(p => p.FileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPageHasATitleADescriptionAndItsOwnAddress()
    {
        Assert.All(WikiDocs.Pages, page =>
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title));
            Assert.InRange(page.Description.Length, 1, 160);
            Assert.Same(page, WikiDocs.Find(page.Slug));
            Assert.StartsWith(WikiDocs.BasePath, WikiDocs.PathOf(page));
            Assert.EndsWith("/docs/wiki/" + page.FileName, WikiDocs.SourceUrl(page));
            Assert.DoesNotContain("<h1", page.Html);
        });
        Assert.Null(WikiDocs.Find("api"));
        Assert.Null(WikiDocs.Find("../README"));
    }

    [Fact]
    public void EveryLinkBetweenWikiPagesStaysOnTheSite()
    {
        var hrefs = WikiDocs.Pages.SelectMany(p => System.Text.RegularExpressions.Regex.Matches(p.Html, "href=\"([^\"]*)\"").Select(m => m.Groups[1].Value)).ToList();
        Assert.All(hrefs, href => Assert.True(href.StartsWith(WikiDocs.BasePath, StringComparison.Ordinal) || href.StartsWith("https://", StringComparison.Ordinal) || href.StartsWith("http://", StringComparison.Ordinal), href));
        Assert.All(hrefs.Where(h => h.Split('#')[0].EndsWith(".md", StringComparison.Ordinal)), href => Assert.StartsWith(SiteSeo.RepositoryUrl + "/blob/master/", href));
        Assert.Contains("href=\"/docs/02-review-pipeline\"", WikiDocs.Find("")!.Html);
    }

    [Theory]
    [InlineData("#limits", "01-system-overview", "/docs/01-system-overview#limits")]
    [InlineData("#limits", "", "/docs#limits")]
    [InlineData("06-web-app-and-operations.md", "01-system-overview", "/docs/06-web-app-and-operations")]
    [InlineData("06-web-app-and-operations.md#ci", "01-system-overview", "/docs/06-web-app-and-operations#ci")]
    [InlineData("README.md", "01-system-overview", "/docs")]
    [InlineData("https://doi.org/10.5281/zenodo.24379094", "", "https://doi.org/10.5281/zenodo.24379094")]
    [InlineData("../roadmap/redesign.md", "", "https://github.com/lauPhilip/au-btech-literature-review-agent/blob/master/docs/roadmap/redesign.md")]
    [InlineData("../../AuBtechReviewAgent/Web/SiteSeo.cs#L10", "", "https://github.com/lauPhilip/au-btech-literature-review-agent/blob/master/AuBtechReviewAgent/Web/SiteSeo.cs#L10")]
    public void LinksAreRewrittenForTheSite(string url, string slug, string expected) =>
        Assert.Equal(expected, WikiDocs.Rewrite(url, slug, Files));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("/etc/passwd")]
    [InlineData("//evil.example.org/x")]
    [InlineData("../../../outside.md")]
    [InlineData("")]
    public void UnsafeOrUnknownAddressesAreDropped(string url) => Assert.Null(WikiDocs.Rewrite(url, "", Files));

    [Fact]
    public void RawHtmlIsShownAsTextAndUnsafeLinksKeepOnlyTheirWords()
    {
        var page = WikiDocs.Render("09-x.md",
            "# Title\n\nFirst paragraph.\n\n<script>alert(1)</script>\n\n[click](javascript:alert(1)) <javascript:alert(2)> " +
            "![shot](https://example.org/a.png)\n\n## A section\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```mermaid\ngraph TD; A-->B\n```\n", Files);

        Assert.Equal("Title", page.Title);
        Assert.Equal("First paragraph.", page.Description);
        Assert.DoesNotContain("<script", page.Html);
        Assert.Contains("&lt;script&gt;", page.Html);
        Assert.DoesNotContain("href=\"javascript", page.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>click javascript:alert(2) <a href=", page.Html);
        Assert.DoesNotContain("<img", page.Html);
        Assert.Contains("<a href=\"https://example.org/a.png\">shot</a>", page.Html);
        Assert.Equal(new[] { ("a-section", "A section") }, page.Sections);
        Assert.Contains("<table tabindex=\"0\"", page.Html);
        Assert.Contains("language-mermaid", page.Html);
    }

    [Fact]
    public void ALongFirstParagraphIsCutAtAWord()
    {
        var page = WikiDocs.Render("x.md", "# T\n\n" + string.Join(' ', Enumerable.Repeat("word", 60)), Files);
        Assert.True(page.Description.Length <= 160);
        Assert.EndsWith("word…", page.Description);
    }
}

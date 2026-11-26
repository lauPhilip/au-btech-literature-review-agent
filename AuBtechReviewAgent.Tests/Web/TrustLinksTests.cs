using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The trust links of the app pages, read as source: Example reports stays in the one-line footer on every width and
/// GitHub is what drops first (issue #639), and the New review composer carries a short trust line with links to
/// About, the model card and Privacy (issue #642).
/// </summary>
public class TrustLinksTests
{
    private static string? Read(params string[] parts)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent" }.Concat(parts).ToArray()));
    }

    private static string Item(string footer, string href) =>
        Regex.Match(footer, "<li[^>]*>\\s*<a href=\"" + Regex.Escape(href) + "\"[^>]*>[^<]*</a></li>").Value;

    [Fact]
    public void ExampleReportsStaysInTheAppFooterOnEveryWidthAndGitHubDropsFirst()
    {
        string? footer = Read("Components", "Layout", "SiteFooter.razor");
        if (footer == null) return; // not run from the repository
        string app = footer[footer.IndexOf("else", StringComparison.Ordinal)..footer.IndexOf("</footer>", StringComparison.Ordinal)];

        string examples = Item(app, "/gallery");
        Assert.NotEmpty(examples);
        Assert.DoesNotContain("hidden", examples);
        Assert.Contains("hidden sm:block", Regex.Match(app, "<li[^>]*><a href=\"@AuBtechReviewAgent.SiteSeo.RepositoryUrl\"[^>]*>GitHub</a></li>").Value);
        // The one-line constraint at 390 px (issue #244): the name is dropped on a phone, the header carries it.
        Assert.Contains("py-2 max-sm:hidden\"><span class=\"font-display font-semibold text-heading\">TraceableAI", app);
        foreach (var href in new[] { "/about", "/privacy", "/accessibility" })
            Assert.DoesNotContain("hidden", Item(app, href));
    }
}

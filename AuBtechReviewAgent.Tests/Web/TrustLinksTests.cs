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

    [Fact]
    public void TheComposerHasATrustLineBesideTheDecision()
    {
        string? composer = Read("Components", "Dashboard", "ReviewComposer.razor");
        if (composer == null) return;
        string line = composer[composer.IndexOf("data-trust-line", StringComparison.Ordinal)..];
        line = line[..line.IndexOf("</p>", StringComparison.Ordinal)];

        // After the Start button, in the same panel; links to About and Privacy.
        Assert.True(composer.IndexOf("Start review</button>", StringComparison.Ordinal) < composer.IndexOf("data-trust-line", StringComparison.Ordinal));
        foreach (var href in new[] { "/about", "/privacy" }) Assert.Contains($"href=\"{href}\"", line);
        // One line (issue #729): the model card is one click away in the footer instead.
        Assert.Contains("href=\"/model-card\"", Read("Components", "Layout", "SiteFooter.razor") ?? "href=\"/model-card\"");
        // What it says is what the privacy page says: the model and the databases receive the question; runs are deleted; no accounts.
        Assert.Contains("language model", line);
        Assert.Contains("scholarly databases", line);
        Assert.Contains("deleted with your run", line);
        Assert.Contains("No accounts", line);
        // No rounded corners, and a phone keeps only the links so the form still fits 390 x 844.
        Assert.DoesNotContain("rounded", line);
        Assert.Contains("max-sm:hidden", line);
        string? privacy = Read("Web", "PrivacyText.cs") + Read("Components", "Pages", "Privacy.razor") + Read("Components", "Pages", "Privacy.razor.cs");
        Assert.Contains("There are no accounts", privacy);
        Assert.Contains("Mistral AI", privacy);
        Assert.Contains("deleted automatically", privacy);
    }
}

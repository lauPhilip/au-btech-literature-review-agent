using AuBtechReviewAgent.Components.Pages;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The privacy page names who is responsible, the rights and where to complain (issue #543), and the accessibility
/// statement follows the order of Digitaliseringsstyrelsen's WAS-Tool statement (issue #544). The pages are read as
/// source, as in <see cref="DocPageTests"/>; the words in code are checked directly.
/// </summary>
public class LegalPagesTests
{
    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root;
    }

    private static string Page(string root, string file) =>
        File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Components", "Pages", file));

    [Fact]
    public void ThePrivacyPageNamesTheControllerTheRightsAndTheAuthority()
    {
        Assert.Equal("Department of Business Development and Technology (BTECH), Aarhus University", Privacy.Controller);
        Assert.Equal(SiteSeo.RepositoryUrl + "/issues", Privacy.ContactUrl);
        Assert.StartsWith("https://www.datatilsynet.dk", Privacy.DatatilsynetUrl);

        string? root = Root();
        if (root == null) return; // not run from the repository (packaged tests)
        string page = Page(root, "Privacy.razor");
        foreach (var id in new[] { "controller", "retention", "third-parties", "rights", "choices" })
            Assert.Contains($"id=\"{id}\"", page);
        Assert.Contains("Datatilsynet", page);
    }

    [Fact]
    public void ThePrivacyTextSaysWhatTheCodeDoes()
    {
        string address = string.Join(" ", PrivacyText.Address(3));
        Assert.Contains("3 per day", address);
        Assert.Contains("never written to disk", address);
        Assert.Contains("never the address itself", address);
        Assert.DoesNotContain("cannot be turned back into an address.", address); // only without the secret

        // SessionCleanupWorker measures a run's age from its newest file, not from when it was created.
        string review = string.Join(" ", PrivacyText.Review(7, grey: false));
        Assert.Contains("7 days after it last changed", review);
        Assert.DoesNotContain("grey literature", review);

        string gallery = string.Join(" ", PrivacyText.Gallery(7, 50));
        Assert.Contains("records the time of that yes", gallery);
        Assert.Contains("withdraw", gallery);
    }

    [Fact]
    public void TheAccessibilityStatementFollowsTheWasToolOrder()
    {
        Assert.NotEmpty(Accessibility.Gaps);
        Assert.All(Accessibility.Gaps, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Problem));
            Assert.True(g.Criterion == "Not assessed" || char.IsDigit(g.Criterion[0]), g.Criterion);
        });
        Assert.StartsWith("https://digst.dk/", Accessibility.EnforcementUrl);

        string? root = Root();
        if (root == null) return;
        string page = Page(root, "Accessibility.razor");
        Assert.Contains("@page \"/accessibility\"", page);
        Assert.Contains("partially compliant", page);
        Assert.Contains("WCAG 2.1 level AA", page);
        Assert.Contains("EN 301 549", page);
        Assert.Contains("Digitaliseringsstyrelsen", page);
        Assert.Contains("was.digst.dk", page);
        // Scope, status, gaps, method (with the date), feedback, enforcement: in that order.
        int last = -1;
        foreach (var id in new[] { "scope", "status", "gaps", "method", "feedback", "enforcement" })
        {
            int at = page.IndexOf($"<h2 id=\"{id}\"", StringComparison.Ordinal);
            Assert.True(at > last, id);
            last = at;
        }
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Planning that matches the searches (#249): a live run named five kinds of grey literature but searched only GitHub,
/// so every source was a code repository. The plan now says which chosen search can find each kind, and warns when none can.
/// </summary>
public class GreySearchReachTests
{
    [Fact]
    public void TheKindsEachSearchFindsAreTheKindsItsSourcesAreGiven()
    {
        // The kinds the parsers set, checked against recorded answers in GreySourceTests; here the table itself.
        Assert.Equal(new[] { "code" }, GreySourceCatalog.KindsFoundBy("github"));
        Assert.Equal(new[] { "qa" }, GreySourceCatalog.KindsFoundBy("stackexchange"));
        Assert.Contains("blogs", GreySourceCatalog.KindsFoundBy("hackernews"));
        Assert.Empty(GreySourceCatalog.KindsFoundBy("backlinks")); // not built yet
        Assert.Empty(GreySourceCatalog.KindsFoundBy("unknown"));
        foreach (var search in MultivocalGuidelines.GreySearches)
            Assert.All(GreySourceCatalog.KindsFoundBy(search.Key), k => Assert.Contains(k, MultivocalGuidelines.GreyTypes.Select(t => t.Key)));
    }

    [Fact]
    public void TheLiveRunsPlanWarnsForEveryKindGitHubCannotFind()
    {
        var reach = GreySourceCatalog.Reach(new[] { "blogs", "documentation", "code", "white-papers", "qa" }, new[] { "github" });

        Assert.Equal(new[] { "github" }, reach.Single(r => r.Kind == "code").Searches);
        Assert.Equal(new[] { "blogs", "documentation", "white-papers", "qa" }, reach.Where(r => r.Searches.Count == 0).Select(r => r.Kind));
    }

    [Fact]
    public void EveryKindAPlanCanNameIsFoundBySomeSearchExceptNewsAndTalks()
    {
        var all = MultivocalGuidelines.GreySearches.Select(s => s.Key);
        var unreached = GreySourceCatalog.Reach(MultivocalGuidelines.GreyTypes.Select(t => t.Key), all).Where(r => r.Searches.Count == 0).Select(r => r.Kind);

        Assert.Equal(new[] { "news", "talks" }, unreached);
    }

    [Fact]
    public void TheProtocolSaysWhichSearchFindsEachKindAndWarnsForTheRest()
    {
        var plan = MultivocalPlan.Example("grey");
        plan.GreyTypes = new List<string> { "blogs", "code" };
        plan.GreySearches = new List<string> { "github" };

        string protocol = MultivocalProtocol.Write(plan, Guid.NewGuid(), new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("Which chosen search can find each kind (G6, G7):", protocol);
        Assert.Contains("- Code repositories and their README files: GitHub repositories.", protocol);
        Assert.Contains("- Blog posts and practitioner articles: none of the chosen searches can find this kind, so the review will likely include none.", protocol);
        Assert.Equal(new[] { true, false }, MultivocalProtocol.ReachLines(plan).Select(l => l.Warning)); // in the plan's order: blogs, code
    }

    [Fact]
    public void ThePlanningFormShowsTheSameLinesWithTheWarningsMarked()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository
        string step = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Modules", "Multivocal", "Pages", "MultivocalSearchStep.razor"));

        Assert.Contains("MultivocalProtocol.ReachLines(Page.plan)", step);
        Assert.Contains("What the chosen searches can find", step);
        Assert.Contains("\"Warning: \"", step); // not by colour alone
        Assert.Contains("role=\"status\"", step);
    }
}

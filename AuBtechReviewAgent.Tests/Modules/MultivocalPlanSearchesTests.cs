using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #474: the example plan ticked backlink snowballing, which is not built, so the protocol promised a search the
/// tool cannot run. Issue #486: under the saturation rule a hidden field still capped every search at 20 hits, which
/// the form never showed and the protocol never stated.
/// </summary>
public class MultivocalPlanSearchesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlr-plan-searches-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Theory]
    [InlineData("grey")]
    [InlineData("multivocal")]
    public void NoPlanStartsWithASearchThatIsNotBuilt(string card)
    {
        Assert.All(new MultivocalPlan().GreySearches, k => Assert.True(GreySourceCatalog.IsBuilt(k), k));
        Assert.All(MultivocalPlan.Example(card).GreySearches, k => Assert.True(GreySourceCatalog.IsBuilt(k), k));
    }

    [Fact]
    public void CleaningThePlanLeavesOutASearchThatIsNotBuiltAndProblemsNameIt()
    {
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new() { "stackexchange", "backlinks" };
        Assert.Contains("Links in the included grey sources (backlink snowballing) is not available yet; leave it out.", plan.Problems());

        plan.Normalized();
        Assert.Equal(new[] { "stackexchange" }, plan.GreySearches);
        Assert.Empty(plan.Problems());
    }

    [Fact]
    public async Task TheProtocolDoesNotPromiseBacklinkSnowballing()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches.Add("backlinks");
        var (runId, _) = await planner.CreateAsync(plan);

        Assert.DoesNotContain("backlinks", planner.Load(runId)!.Plan.GreySearches);
        string protocol = File.ReadAllText(Path.Join(store.FolderOf(runId), RunStore.ProtocolFile));
        Assert.DoesNotContain("backlink", protocol, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnderSaturationTheTopHitsAreCheckedAndStatedInTheProtocol()
    {
        var plan = MultivocalPlan.Example();
        plan.StoppingRule = "saturation";
        plan.TopHits = 40;
        Assert.True(plan.UsesTopHits);
        Assert.Contains("Stopping rule (G8): Theoretical saturation: stop when new sources add nothing new; the strings are searched in order, up to the top 40 hits of each.",
            MultivocalProtocol.Write(plan, Guid.NewGuid(), DateTime.UtcNow));

        plan.TopHits = 5;
        Assert.Contains("Look at between 10 and 500 top hits per search string.", plan.Problems());
    }

    [Fact]
    public void UnderExhaustionTheTopHitsPlayNoPart()
    {
        var plan = MultivocalPlan.Example();
        plan.StoppingRule = "exhaustion";
        plan.TopHits = 5;
        Assert.False(plan.UsesTopHits);
        Assert.Empty(plan.Problems());
    }

    [Fact]
    public async Task TheSaturationSearchTakesTheHitsThePlanStatesAndTheLedgerSaysSo()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.StoppingRule = "saturation";
        plan.TopHits = 40;
        plan.GreySearches = new() { "stackexchange" };
        var (runId, key) = await planner.CreateAsync(plan);
        var asked = new List<int>();
        var source = new MultivocalSearchTests.FakeGreySource("stackexchange", (q, max) => { asked.Add(max); return new(); });

        var ledger = await new MultivocalSearcher(store, planner, k => k == "stackexchange" ? source : null).SearchAsync(runId, key);

        Assert.All(asked, max => Assert.Equal(40, max));
        Assert.Contains("up to 40 hits each", ledger.StoppingApplied);
    }

    [Fact]
    public void TheFormShowsTheFieldForSaturationAndMarksASearchThatIsNotBuilt()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository
        string step = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Modules", "Multivocal", "Pages", "MultivocalSearchStep.razor"));

        Assert.Contains("@if (Page.plan.UsesTopHits)", step);
        Assert.Contains("GreySourceCatalog.IsBuilt(s.Key)", step);
        Assert.Contains("(not available yet)", step);
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The kinds of review on the start page: which can run, and how a run records its kind.</summary>
public class ReviewMethodTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "methods-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public void TheSystematicAndTheGreyLiteratureReviewAreAvailable()
    {
        Assert.Equal(new[] { "systematic", "multivocal", "rapid", "living", "grey" }, ReviewMethods.All.Select(m => m.Key));
        Assert.Equal(new[] { "systematic", "grey" }, ReviewMethods.All.Where(m => m.Available).Select(m => m.Key));
        Assert.False(ReviewMethods.Find("multivocal")!.Available); // stays off until the formal pool is built (block I)
        Assert.All(ReviewMethods.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Standard));
            Assert.StartsWith("https://", m.StandardUrl);
            Assert.NotEmpty(m.Highlights);
        });
    }

    [Theory]
    [InlineData(null, "systematic")]
    [InlineData("SYSTEMATIC", "systematic")]
    [InlineData("living", "systematic")]   // coming soon: the form falls back to what can run
    [InlineData("narrative", "systematic")] // unknown
    [InlineData("grey", "systematic")]      // on, but run on its own pages, never by the systematic form
    public void TheFormAlwaysGetsAMethodThatCanRun(string? key, string expected) =>
        Assert.Equal(expected, ReviewMethods.AvailableOrDefault(key).Key);

    [Fact]
    public void AComingSoonMethodIsRefusedBeforeAnythingIsSpent()
    {
        var request = new ReviewRequest("agent loops", "", "Agent architecture", "", 3, Method: "multivocal");
        var check = ReviewInputGuard.Check(request);
        Assert.Contains(check.Errors, e => e.Contains("not available yet"));
        Assert.True(ReviewInputGuard.Check(request with { Method = "systematic" }).IsValid);
    }

    [Fact]
    public void ACardOfAnotherModuleIsNeverRunByTheSystematicEngine()
    {
        var request = new ReviewRequest("agent loops", "", "Agent architecture", "", 3, Method: "grey");
        var check = ReviewInputGuard.Check(request);
        Assert.Contains(check.Errors, e => e.Contains("started from its own page"));
        Assert.False(ReviewMethods.RunsHere(ReviewMethods.Find("grey")!));
        Assert.True(ReviewMethods.RunsHere(ReviewMethods.Default));
    }

    [Fact]
    public async Task ARunRecordsItsMethodInTheLedgerAndProtocol()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, DualScreening: false));

        Assert.Equal("systematic", engine.LoadState(runId)!.Method);
        string protocol = File.ReadAllText(Path.Join(_root, runId.ToString("N"), "protocol.md"));
        Assert.Contains("Review type: Systematic Literature Review, reported following PRISMA 2020", protocol);
    }
}

using AuBtechReviewAgent;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Cancelling a grey literature run through the engine's one entry point (issue #233): the step that is running
/// finishes, the run stops before the next one, and run.json keeps when and why.
/// </summary>
public class MultivocalCancelTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrcancel-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            new("x:1", "Managing agent context", "A post.", "https://blog.example.org/post", "Ana", "blog.example.org", "blogs", new DateTime(2025, 5, 1), new Dictionary<string, long>()),
        });
    }

    [Fact]
    public async Task ARunningGreyRunStopsBeforeItsNextStepAndCanContinueLater()
    {
        var engine = new PrismaReviewEngine("test-key", "") { WorkspaceRoot = _root };
        var store = engine.Store;
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);

        // No step after the search runs, so the model is never asked.
        Func<IChatCompletionService> chat = () => new FakeChatService();
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        var pages = new MultivocalPages(store, searcher, new PageFetcher());
        var screener = new MultivocalScreener(store, planner, searcher, chat);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, chat);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, chat);
        var runner = new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages);
        engine.AddCanceller(runner.Cancel);

        bool asked = false, withoutKey = true, withKey = false, twice = true;
        runner.Changed += id =>
        {
            if (asked || runner.StatusOf(id) is not { Running: true, Step: "Searching" }) return;
            asked = true;
            withoutKey = engine.CancelRun(runId, "a-wrong-key", "Not mine");
            withKey = engine.CancelRun(runId, key, "Wrong sources");
            twice = engine.CancelRun(runId, key, "Again");
        };
        await runner.RunAsync(runId, key);

        Assert.True(asked);
        Assert.False(withoutKey);
        Assert.True(withKey);
        Assert.False(twice);
        var status = runner.StatusOf(runId)!;
        Assert.False(status.Running);
        Assert.Equal(PrismaReviewEngine.StageCancelled, status.Step);
        Assert.Contains("Wrong sources", status.Error);

        // The search finished and the run stopped before the screening, which is where it continues.
        var header = store.LoadHeader(runId)!;
        Assert.Equal(MultivocalSearcher.StageSearched, header.Stage);
        Assert.True(MultivocalRunner.HasStepsLeft(header.Stage));
        Assert.Equal("Wrong sources", header.CancelReason);
        Assert.NotNull(header.CancelledUtc);
        Assert.False(engine.CancelRun(runId, key, "Not running"));
    }
}

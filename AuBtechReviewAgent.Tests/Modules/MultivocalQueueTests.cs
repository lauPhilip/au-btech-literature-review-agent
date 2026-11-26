using AuBtechReviewAgent;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A grey literature run waits in the same line as a systematic review: it shows its place, starts when a slot frees,
/// leaves the line when its owner cancels it, and is refused at once when the line is full.
/// </summary>
public class MultivocalQueueTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrqueue-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>());
    }

    private async Task<(PrismaReviewEngine Engine, MultivocalRunner Runner, Guid RunId, string Key)> SetUpAsync(RunsOptions options)
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, options) { WorkspaceRoot = _root };
        var store = engine.Store;
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        Func<IChatCompletionService> chat = () => new FakeChatService();
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        var pages = new MultivocalPages(store, searcher, new PageFetcher());
        var screener = new MultivocalScreener(store, planner, searcher, chat);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, chat);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, chat);
        var runner = new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages) { Coordinator = engine.Coordinator };
        engine.AddCanceller(runner.Cancel);
        return (engine, runner, runId, key);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task AGreyRunWaitsForASlotThenRunsAndCanLeaveTheLine()
    {
        var (engine, runner, runId, key) = await SetUpAsync(new RunsOptions { MaxConcurrentRuns = 1 });
        var systematic = Guid.NewGuid();
        var held = await engine.Coordinator.AcquireSlotAsync(systematic);

        Assert.True(runner.Start(runId, key, "192.0.2.1"));
        await WaitUntil(() => runner.StatusOf(runId)?.Progress?.StartsWith("You are 1st in line") == true);
        Assert.Equal(MultivocalRunner.StepWaiting, runner.StatusOf(runId)!.Step);
        Assert.Equal(MultivocalPlanner.StagePlanned, engine.Store.LoadHeader(runId)!.Stage);
        // A run in line counts as running: it cannot be deleted, noted or cleaned up, and is not a systematic run.
        Assert.True(engine.Store.IsActive(runId));
        Assert.False(engine.DeleteRun(runId, key));
        Assert.DoesNotContain(runId, engine.Coordinator.ActiveRunIds);

        Assert.True(engine.CancelRun(runId, key, "Too long a wait"));
        await WaitUntil(() => runner.StatusOf(runId)?.Running == false);
        Assert.Equal(PrismaReviewEngine.StageCancelled, runner.StatusOf(runId)!.Step);
        Assert.Equal(0, engine.Coordinator.Queued);
        Assert.Equal(MultivocalPlanner.StagePlanned, engine.Store.LoadHeader(runId)!.Stage);

        // Started again, it waits until the systematic run gives its slot back, then searches.
        Assert.True(runner.Start(runId, key, "192.0.2.1"));
        await WaitUntil(() => runner.StatusOf(runId)?.Step == MultivocalRunner.StepWaiting);
        held.Dispose();
        await WaitUntil(() => runner.StatusOf(runId)?.Running == false);
        Assert.NotEqual(MultivocalPlanner.StagePlanned, engine.Store.LoadHeader(runId)!.Stage);
        Assert.Equal((0, 0), (engine.Coordinator.Running, engine.Coordinator.Queued));
        await WaitUntil(() => !engine.Store.IsActive(runId)); // once its steps end it can be deleted again
        Assert.True(engine.DeleteRun(runId, key));
    }

    [Fact]
    public async Task AFullLineRefusesTheGreyRunWithAPlainReason()
    {
        var (engine, runner, runId, key) = await SetUpAsync(new RunsOptions { MaxConcurrentRuns = 1, MaxQueuedRuns = 1 });
        using var held = await engine.Coordinator.AcquireSlotAsync(Guid.NewGuid());
        Assert.True(engine.Coordinator.TryAdmit(Guid.NewGuid(), "198.51.100.1").Admitted);

        Assert.False(runner.Start(runId, key, "192.0.2.1"));
        var status = runner.StatusOf(runId)!;
        Assert.False(status.Running);
        Assert.StartsWith("The tool is busy", status.Error);
        Assert.False(engine.Store.IsActive(runId)); // a refused run is not held
    }
}

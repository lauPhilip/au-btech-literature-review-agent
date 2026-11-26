using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Cancelling a systematic review (issue #233), with the scripted fake model. The model waits at a gate, so a run can
/// be caught while it holds its slot and another waits in line.
/// </summary>
public class CancelRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "cancel-" + Guid.NewGuid().ToString("N"));
    private readonly string _quotaRoot = Path.Join(Path.GetTempPath(), "cancel-quota-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _open = new(false);
    private readonly ManualResetEventSlim _entered = new(false);

    public void Dispose()
    {
        _open.Set();
        TestFolders.TryDelete(_root);
        TestFolders.TryDelete(_quotaRoot);
    }

    private PrismaReviewEngine Engine() => new("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(prompt =>
        {
            _entered.Set();
            _open.Wait(TimeSpan.FromSeconds(30));
            return PipelineTests.Respond(prompt);
        }),
        SourceFactory = _ => new PipelineTests.FakeSource(),
        FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
    };

    private static ReviewRequest Request(bool humanReview = false) => new(
        "agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
        SelectedSources: new[] { "arxiv" }, HumanScreeningReview: humanReview);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task AQueuedRunThatIsCancelledLeavesTheQueue()
    {
        var engine = Engine();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        engine.ClaimRun(a);
        string keyB = engine.ClaimRun(b);
        var runA = engine.RunReviewAsync(a, Request());
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(20)));
        var runB = engine.RunReviewAsync(b, Request());
        await WaitUntil(() => engine.Coordinator.QueuePosition(b) == 1);

        Assert.True(engine.CancelRun(b, keyB, "Wrong question"));
        await Assert.ThrowsAsync<RunCancelledException>(() => runB.WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(0, engine.Coordinator.QueuePosition(b));
        Assert.False(engine.IsRunActive(b));
        var state = engine.LoadState(b)!;
        Assert.Equal(PrismaReviewEngine.StageCancelled, state.Stats.ProcessingStage);
        Assert.Equal("Wrong question", state.CancelReason);
        Assert.NotNull(state.CancelledUtc);
        var header = engine.LoadHeader(b)!;
        Assert.Equal((PrismaReviewEngine.StageCancelled, "Wrong question"), (header.Stage, header.CancelReason));
        Assert.True(Directory.Exists(Path.Join(_root, b.ToString("N"))), "the folder is kept until the normal clean-up");

        _open.Set();
        await runA.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PrismaReviewEngine.StageComplete, engine.LoadState(a)!.Stats.ProcessingStage);
    }

    [Fact]
    public async Task ARunningRunStopsAtTheNextSafePointFreesItsSlotAndGetsItsQuotaBack()
    {
        var quota = new RunQuotaService(new QuotaOptions { FreeRunsPerDay = 1, GlobalFreeRunsPerDay = 50 }, false, _quotaRoot);
        Assert.True(quota.TryReserve("1.2.3.4", QuotaTier.Free, out _, out var lease));
        var engine = Engine();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string keyA = engine.ClaimRun(a);
        engine.ClaimRun(b);
        var runA = engine.RunReviewAsync(a, Request());
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(20)));
        var runB = engine.RunReviewAsync(b, Request());
        await WaitUntil(() => engine.Coordinator.QueuePosition(b) == 1);

        DateTime asked = DateTime.UtcNow;
        Assert.True(engine.CancelRun(a, keyA, "Started it by mistake"));
        Assert.False(engine.CancelRun(a, keyA, "Twice"));
        _open.Set();
        var ended = await Assert.ThrowsAsync<RunCancelledException>(() => runA.WaitAsync(TimeSpan.FromSeconds(30)));

        var state = engine.LoadState(a)!;
        Assert.Equal(PrismaReviewEngine.StageCancelled, state.Stats.ProcessingStage);
        Assert.Equal("Started it by mistake", state.CancelReason);
        Assert.InRange(state.CancelledUtc!.Value, asked, state.CompletedUtc!.Value);
        Assert.Contains("Started it by mistake", state.FailureMessage);
        Assert.False(File.Exists(Path.Join(_root, a.ToString("N"), "prisma-report.json")), "no report is written for a run stopped in screening");
        Assert.False(engine.IsRunActive(a));

        // The slot is free again: the run that waited in line finishes.
        await runB.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PrismaReviewEngine.StageComplete, engine.LoadState(b)!.Stats.ProcessingStage);

        Assert.True(quota.GiveBackAfter(lease!, ended, engine.LoadState(a)!.RunSettings!.LlmCalls));
        Assert.Equal(1, quota.GetStatus("1.2.3.4", QuotaTier.Free).Remaining);
    }

    [Fact]
    public async Task ARunCancelledWhileFetchingFullTextsStopsThereInsteadOfFinishingEveryDownload()
    {
        _open.Set();
        using var fetching = new ManualResetEventSlim(false);
        var never = new TaskCompletionSource<FullTextResult>(); // a download that would never end on its own
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => { fetching.Set(); return never.Task; },
        };
        var runId = Guid.NewGuid();
        string key = engine.ClaimRun(runId);
        var run = engine.RunReviewAsync(runId, Request());
        Assert.True(fetching.Wait(TimeSpan.FromSeconds(30)), "the run reached the full-text step");

        Assert.True(engine.CancelRun(runId, key, "Too slow"));
        await Assert.ThrowsAsync<RunCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageCancelled, state.Stats.ProcessingStage);
        Assert.Empty(state.FullTextSources);
        Assert.False(File.Exists(Path.Join(_root, runId.ToString("N"), "prisma-report.json")));
    }

    [Fact]
    public async Task ARunWaitingForTheHumanCheckStopsWaiting()
    {
        _open.Set();
        var engine = Engine();
        var runId = Guid.NewGuid();
        string key = engine.ClaimRun(runId);
        var run = engine.RunReviewAsync(runId, Request(humanReview: true));
        await WaitUntil(() => engine.IsAwaitingScreeningReview(runId));

        Assert.True(engine.CancelRun(runId, key, ""));
        await Assert.ThrowsAsync<RunCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageCancelled, state.Stats.ProcessingStage);
        Assert.Equal(PrismaReviewEngine.NoCancelReason, state.CancelReason);
        Assert.Null(state.HumanScreeningReviewOutcome); // no claim that the wait timed out
    }

    [Fact]
    public async Task AFinishedRunCannotBeCancelledAndKeepsItsFilesAsBefore()
    {
        _open.Set();
        var engine = Engine();
        var runId = Guid.NewGuid();
        string key = engine.ClaimRun(runId);
        await engine.RunReviewAsync(runId, Request());

        Assert.False(engine.CancelRun(runId, key, "Too late"));
        Assert.Equal(PrismaReviewEngine.StageComplete, engine.LoadState(runId)!.Stats.ProcessingStage);
        string folder = Path.Join(_root, runId.ToString("N"));
        Assert.DoesNotContain("Cancel", File.ReadAllText(Path.Join(folder, "transparent-process.json")));
        Assert.DoesNotContain("Cancel", File.ReadAllText(Path.Join(folder, RunHeader.FileName)));
    }

    [Fact]
    public async Task WithoutTheEditKeyNothingHappens()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        engine.ClaimRun(runId);
        var run = engine.RunReviewAsync(runId, Request());
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(20)));

        Assert.False(engine.CancelRun(runId, null, "Not mine"));
        Assert.False(engine.CancelRun(runId, "a-wrong-key", "Not mine"));
        _open.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        Assert.Null(state.CancelReason);
    }

    [Theory]
    [InlineData(null, PrismaReviewEngine.NoCancelReason)]
    [InlineData("  ", PrismaReviewEngine.NoCancelReason)]
    [InlineData("Wrong\nquestion", "Wrong question")]
    public void TheReasonIsKeptAsOneLineOfText(string? reason, string kept) =>
        Assert.Equal(kept, PrismaReviewEngine.CleanCancelReason(reason));

    [Fact]
    public void ALongReasonIsCut() =>
        Assert.Equal(PrismaReviewEngine.MaxCancelReasonLength, PrismaReviewEngine.CleanCancelReason(new string('x', 1000)).Length);
}

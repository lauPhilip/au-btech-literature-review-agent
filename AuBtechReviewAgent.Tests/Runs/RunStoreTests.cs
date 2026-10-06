using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The run store: run folders, edit keys, notes and deleting, without a review engine.</summary>
public class RunStoreTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "runstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static RunHeader Header(Guid runId) =>
        new(runId, "systematic", "systematic", DateTime.UtcNow, PrismaReviewEngine.StageComplete, DateTime.UtcNow);

    private async Task<Guid> StartedRunAsync(RunStore store, Guid? id = null)
    {
        var runId = id ?? Guid.NewGuid();
        Directory.CreateDirectory(store.FolderOf(runId));
        await RunHeader.WriteAsync(store.FolderOf(runId), Header(runId));
        return runId;
    }

    [Fact]
    public void ARunFolderIsAlwaysInsideTheWorkspace()
    {
        var store = new RunStore(_root);
        var runId = Guid.NewGuid();
        string folder = store.FolderOf(runId);
        Assert.Equal(Path.Join(Path.GetFullPath(_root), runId.ToString("N")), folder);
    }

    [Fact]
    public async Task ARunHasStartedWhenItsFolderHasAHeaderOrAnOlderLedger()
    {
        var store = new RunStore(_root);
        var claimed = Guid.NewGuid();
        store.ClaimRun(claimed); // folder with only owner.json: not started
        Assert.False(store.HasRun(claimed));

        var started = await StartedRunAsync(store);
        Assert.True(store.HasRun(started));

        var older = Guid.NewGuid();
        Directory.CreateDirectory(store.FolderOf(older));
        File.WriteAllText(Path.Join(store.FolderOf(older), "transparent-process.json"), "{}");
        Assert.True(store.HasRun(older));
        Assert.Equal("systematic", store.LoadHeader(older)!.Module);
    }

    [Fact]
    public async Task NotesNeedTheEditKeyAndAStartedRunThatIsNotGoing()
    {
        bool active = false;
        var store = new RunStore(_root, isActive: _ => active);
        var runId = Guid.NewGuid();
        string key = store.ClaimRun(runId);

        Assert.False(await store.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "Checked")); // not started yet

        await StartedRunAsync(store, runId);
        Assert.False(await store.SaveNoteAsync(runId, "wrong-key", ReviewerNote.Study, 1, "Checked"));
        active = true;
        Assert.False(await store.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "Checked"));
        Assert.False(store.DeleteRun(runId, key));
        active = false;

        Assert.True(await store.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "Checked"));
        Assert.Equal("Checked", store.LoadNotes(runId).Single().Text);
        Assert.True(store.DeleteRun(runId, key));
        Assert.False(Directory.Exists(store.FolderOf(runId)));
    }

    [Fact]
    public async Task TheDemoRunIsShownOnlyWhileItExistsAndNoOneCanChangeIt()
    {
        var demo = Guid.NewGuid();
        var store = new RunStore(_root, new RunsOptions { DemoRunId = demo.ToString() });
        Assert.Null(store.DemoRunId);

        await StartedRunAsync(store, demo);
        Assert.Equal(demo, store.DemoRunId);
        Assert.False(store.CanEdit(demo, null));
        Assert.False(store.DeleteRun(demo));
    }

    [Fact]
    public void TheEngineSharesItsStore()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions()) { WorkspaceRoot = _root };
        Assert.Equal(_root, engine.Store.WorkspaceRoot);
        var runId = Guid.NewGuid();
        string key = engine.ClaimRun(runId);
        Assert.True(engine.Store.CanEdit(runId, key));
        Assert.True(engine.Store.HasOwner(runId));
    }
}

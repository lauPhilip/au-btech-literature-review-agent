using System.IO.Compression;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Read-only links with an edit key, the example run, reviewer notes, the tagged RIS exports and the study links.
/// </summary>
public class SharingTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "sharing-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private PrismaReviewEngine Engine(string demoRunId = "") =>
        new("test-key", "", null, null, null, new RunsOptions { DemoRunId = demoRunId })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };

    private static ReviewRequest Request() => new(
        "agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
        SelectedSources: new[] { "arxiv" }, DualScreening: false);

    /// <summary>A finished run started the way the dashboard starts one: claimed first, then run.</summary>
    private static async Task<(Guid RunId, string Key)> OwnedRun(PrismaReviewEngine engine)
    {
        var runId = Guid.NewGuid();
        string key = engine.ClaimRun(runId);
        await engine.RunReviewAsync(runId, Request());
        return (runId, key);
    }

    // ── Edit keys ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyTheEditKeyCanDeleteARun()
    {
        var engine = Engine();
        var (runId, key) = await OwnedRun(engine);

        Assert.True(key.Length >= 40);
        Assert.True(engine.CanEdit(runId, key));
        Assert.False(engine.CanEdit(runId, null));
        Assert.False(engine.CanEdit(runId, key + "x"));
        Assert.False(engine.DeleteRun(runId));
        Assert.False(engine.DeleteRun(runId, "guess"));
        Assert.NotNull(engine.LoadState(runId));
        Assert.True(engine.DeleteRun(runId, key));
        Assert.Null(engine.LoadState(runId));
    }

    [Fact]
    public async Task TheServerKeepsOnlyAHashOfTheKey()
    {
        var engine = Engine();
        var (runId, key) = await OwnedRun(engine);
        string owner = File.ReadAllText(Path.Join(_root, runId.ToString("N"), PrismaReviewEngine.OwnerFile));
        Assert.DoesNotContain(key, owner);
        Assert.Throws<InvalidOperationException>(() => engine.ClaimRun(runId));
    }

    [Fact]
    public async Task RunsFromBeforeEditKeysStayEditable()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request()); // not claimed, like an older run
        Assert.False(engine.HasOwner(runId));
        Assert.True(engine.CanEdit(runId, null));
        Assert.False(engine.CanEdit(Guid.NewGuid(), null)); // a run that does not exist
    }

    [Fact]
    public async Task ARecheckNeedsTheEditKey()
    {
        var engine = Engine();
        var (runId, _) = await OwnedRun(engine);
        var ex = await Assert.ThrowsAsync<PrismaReviewEngine.RecheckException>(() =>
            engine.RecheckCitationAsync(runId, "synthesisResultsItem", 0, 1));
        Assert.Contains("Only the browser that started this run", ex.Message);
    }

    [Fact]
    public void AScreeningReviewNeedsTheEditKey()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        engine.ClaimRun(runId);
        Assert.False(engine.SubmitScreeningReview(runId, Array.Empty<ScreeningOverride>()));
    }

    // ── Example run ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheExampleRunIsShownButCannotBeChanged()
    {
        var first = Engine();
        var (runId, key) = await OwnedRun(first);
        var engine = Engine(runId.ToString());

        Assert.Equal(runId, engine.DemoRunId);
        Assert.True(engine.IsDemoRun(runId));
        Assert.False(engine.CanEdit(runId, key));
        Assert.False(engine.DeleteRun(runId, key));
        Assert.Null(Engine(Guid.NewGuid().ToString()).DemoRunId); // configured, but the run is gone
        Assert.Null(Engine("not a guid").DemoRunId);
    }

    // ── Reviewer notes ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotesAreSavedReplacedAndRemovedWithTheKeyOnly()
    {
        var engine = Engine();
        var (runId, key) = await OwnedRun(engine);

        Assert.False(await engine.SaveNoteAsync(runId, null, ReviewerNote.Study, 1, "No key"));
        Assert.True(await engine.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "  Checked the  full text.  "));
        Assert.True(await engine.SaveNoteAsync(runId, key, ReviewerNote.Citation, 2, "Quote is from the abstract.", "synthesisResultsItem", 0));
        Assert.True(await engine.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "Checked again."));

        var notes = engine.LoadNotes(runId);
        Assert.Equal(2, notes.Count);
        Assert.Equal("Checked again.", notes.Single(n => n.IsFor(ReviewerNote.Study, 1)).Text);
        Assert.Equal("Quote is from the abstract.", notes.Single(n => n.IsFor(ReviewerNote.Citation, 2, "synthesisResultsItem", 0)).Text);

        Assert.True(await engine.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "   "));
        Assert.Single(engine.LoadNotes(runId));

        Assert.False(await engine.SaveNoteAsync(runId, key, "comment", 1, "Unknown kind"));
        Assert.True(await engine.SaveNoteAsync(runId, key, ReviewerNote.Study, 2, new string('x', 5000)));
        Assert.Equal(PrismaReviewEngine.MaxNoteLength, engine.LoadNotes(runId).Single(n => n.IsFor(ReviewerNote.Study, 2)).Text.Length);
    }

    [Fact]
    public async Task TheArchiveHoldsNotesAndTheScreeningButNotTheKey()
    {
        var engine = Engine();
        var (runId, key) = await OwnedRun(engine);
        await engine.SaveNoteAsync(runId, key, ReviewerNote.Study, 1, "Checked.");

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("reviewer-notes.json", names);
        Assert.Contains("screened.ris", names);
        Assert.DoesNotContain(names, n => n.Contains(PrismaReviewEngine.OwnerFile));
    }

    // ── Exports and links ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheScreenedRisTagsEveryRecordWithItsDecision()
    {
        static string Respond(string prompt) =>
            prompt.Contains("Evaluate the following academic paper") && prompt.Contains("- Title: Crop")
                ? """{"decision":"Excluded","reasoning":"Agronomy.","briefSummary":"s","exclusionReason":"off-topic"}"""
                : PipelineTests.Respond(prompt);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request());

        string all = engine.ExportReferences(runId, "screened")!;
        Assert.Equal(3, all.Split("ER  - ").Length - 1);
        Assert.Equal(2, all.Split("KW  - included").Length - 1);
        Assert.Contains("KW  - excluded: off topic", all);
        Assert.Contains("N1  - Reference [1] in the TraceableAI review", all);

        string included = engine.ExportReferences(runId, "ris")!;
        Assert.Contains("KW  - included", included);
        Assert.DoesNotContain("excluded", included);
    }

    [Fact]
    public void OnlyWebAddressesAreLinked()
    {
        Assert.True(PrismaReviewEngine.IsWebAddress("https://arxiv.org/pdf/2401.00001.pdf"));
        Assert.True(PrismaReviewEngine.IsWebAddress("http://example.org/a.pdf"));
        Assert.False(PrismaReviewEngine.IsWebAddress("javascript:alert(1)"));
        Assert.False(PrismaReviewEngine.IsWebAddress("/local/file.pdf"));
        Assert.False(PrismaReviewEngine.IsWebAddress(null));
    }

    [Fact]
    public async Task TheAddressOfADownloadedFullTextIsKept()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (p, _) => Task.FromResult(new FullTextResult(
                new[] { new DocumentChunk(p.Id, 1, "Agent loops recover from faults through supervisor checks.") }, "arXiv", $"https://example.org/{p.Id}.pdf")),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request());

        var state = engine.LoadState(runId)!;
        Assert.Equal(state.Stats.Included, state.FullTextUrls.Count);
        Assert.All(state.FullTextUrls.Values, u => Assert.StartsWith("https://example.org/", u));
    }
}

using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Review modules: which module offers which card, and the run.json header that names a run's module.</summary>
public class ReviewModuleTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "modules-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private PrismaReviewEngine Engine() => new("test-key", "", null, null, null, new RunsOptions())
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
        SourceFactory = _ => new PipelineTests.FakeSource(),
        Cache = ReviewCache.Disabled,
        FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
    };

    [Fact]
    public void EveryCardBelongsToExactlyOneModule()
    {
        var cards = ReviewModules.All.SelectMany(m => m.Cards).ToList();
        Assert.Equal(cards.Count, cards.Select(c => c.Key).Distinct().Count());
        Assert.Equal(ReviewMethods.All.Select(c => c.Key).OrderBy(k => k), cards.Select(c => c.Key).OrderBy(k => k));

        Assert.Equal(new[] { "systematic", "multivocal" }, ReviewModules.All.Select(m => m.Key));
        Assert.Equal(ReviewModules.All.Count, ReviewModules.All.Select(m => m.RoutePrefix).Distinct().Count());
        Assert.All(ReviewModules.All, m =>
        {
            Assert.StartsWith("/", m.RoutePrefix);
            Assert.NotEmpty(m.Cards);
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
        });
    }

    [Theory]
    [InlineData("systematic", "systematic")]
    [InlineData("rapid", "systematic")]      // a variant of the systematic review
    [InlineData("LIVING", "systematic")]
    [InlineData("multivocal", "multivocal")]
    [InlineData("grey", "multivocal")]       // the multivocal review with only the grey pool
    [InlineData("narrative", "systematic")]  // unknown: the default module
    [InlineData(null, "systematic")]
    public void ACardLeadsToTheModuleThatRunsIt(string? card, string module) =>
        Assert.Equal(module, ReviewModules.ForCard(card).Key);

    [Fact]
    public async Task ARunWritesItsHeaderAndTheArchiveKeepsIt()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, DualScreening: false));

        var header = engine.LoadHeader(runId)!;
        Assert.Equal(runId, header.RunId);
        Assert.Equal("systematic", header.Module);
        Assert.Equal("systematic", header.Card);
        Assert.Equal(PrismaReviewEngine.StageComplete, header.Stage);
        Assert.NotNull(header.CompletedUtc);
        Assert.True(header.StartedUtc <= header.CompletedUtc);

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        Assert.NotNull(archive.GetEntry(RunHeader.FileName));
    }

    [Fact]
    public void AnOlderRunWithoutHeaderReadsAsASystematicReview()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        Assert.Null(engine.LoadHeader(runId)); // no folder: no run

        string folder = Path.Join(_root, runId.ToString("N"));
        Directory.CreateDirectory(folder);
        var older = engine.LoadHeader(runId)!;
        Assert.Equal("systematic", older.Module);
        Assert.Equal("systematic", older.Card);

        File.WriteAllText(Path.Join(folder, RunHeader.FileName), "{ not json");
        Assert.Equal("systematic", engine.LoadHeader(runId)!.Module);
    }

    [Fact]
    public void AnInterruptedRunIsMarkedInItsHeaderToo()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        string folder = Path.Join(_root, runId.ToString("N"));
        Directory.CreateDirectory(folder);
        var state = new ReviewState { Stats = new ReviewStats { ProcessingStage = PrismaReviewEngine.StageScreening } };
        File.WriteAllText(Path.Join(folder, "transparent-process.json"), JsonSerializer.Serialize(state));
        File.WriteAllText(Path.Join(folder, RunHeader.FileName),
            new RunHeader(runId, "systematic", "systematic", DateTime.UtcNow, PrismaReviewEngine.StageQueued).ToJson());

        Assert.Equal(1, engine.MarkInterruptedRuns());

        var header = engine.LoadHeader(runId)!;
        Assert.Equal(PrismaReviewEngine.StageInterrupted, header.Stage);
        Assert.NotNull(header.CompletedUtc);
    }
}

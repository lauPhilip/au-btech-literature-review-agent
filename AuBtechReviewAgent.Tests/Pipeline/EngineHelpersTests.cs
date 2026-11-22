using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Pins down the small public helpers of the engine and what a finished run leaves behind (the ledger, the
/// metrics settings and the protocol fingerprint), so the engine's main file can be split safely.
/// </summary>
public class EngineHelpersTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "engine-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    [Theory]
    [InlineData("SCOPUS_ID:1", "ScienceDirect", "Scopus API")]
    [InlineData("IEEE_1", "IEEE", "IEEE Xplore API")]
    [InlineData("OPENALEX_W1", "OpenAlex", "OpenAlex API")]
    [InlineData("S2_1", "SemanticScholar", "Semantic Scholar API")]
    [InlineData("CROSSREF_1", "Crossref", "Crossref API")]
    [InlineData("SCHOLAR_1", "Scholar", "Google Scholar API")]
    [InlineData("RG_1", "ResearchGate", "ResearchGate API")]
    [InlineData("http://arxiv.org/abs/1", "Arxiv", "arXiv API")]
    public void EverySourcePrefixHasItsCategoryAndPlatform(string id, string category, string platform) =>
        Assert.Equal((category, platform), PrismaReviewEngine.SourceOfPaper(id));

    [Theory]
    [InlineData("Arxiv", "arXiv")]
    [InlineData("ScienceDirect", "ScienceDirect")]
    [InlineData("IEEE", "IEEE Xplore")]
    [InlineData("SemanticScholar", "Semantic Scholar")]
    [InlineData("Scholar", "Google Scholar")]
    [InlineData("OpenAlex", "OpenAlex")]
    public void CategoriesHaveDisplayNames(string category, string label) => Assert.Equal(label, PrismaReviewEngine.CategoryLabel(category));

    [Fact]
    public void TheStagesAndWhichOfThemAreFinishedAreFixed()
    {
        Assert.Equal(new[] { "Queued", "Screening", "AwaitingScreeningReview", "Synthesizing", "Complete", "Failed", "Interrupted", "Cancelled" }, new[]
        {
            PrismaReviewEngine.StageQueued, PrismaReviewEngine.StageScreening, PrismaReviewEngine.StageAwaitingReview, PrismaReviewEngine.StageSynthesizing,
            PrismaReviewEngine.StageComplete, PrismaReviewEngine.StageFailed, PrismaReviewEngine.StageInterrupted, PrismaReviewEngine.StageCancelled,
        });
        Assert.True(PrismaReviewEngine.IsFinishedStage("Complete"));
        Assert.True(PrismaReviewEngine.IsFinishedStage("Failed"));
        Assert.True(PrismaReviewEngine.IsFinishedStage("Interrupted"));
        Assert.True(PrismaReviewEngine.IsFinishedStage("Cancelled"));
        Assert.False(PrismaReviewEngine.IsFinishedStage("Synthesizing"));
        Assert.False(PrismaReviewEngine.IsFinishedStage(null));
        Assert.Equal("mistral-large-latest", PrismaReviewEngine.ModelId);
    }

    [Fact]
    public void ASourceNeedsAUsableKeyFromTheServerOrTheUser()
    {
        var engine = new PrismaReviewEngine("m", "", null);
        var none = engine.GetSourceAvailability(null);
        Assert.False(none["scopus"]);
        Assert.False(none["ieee"]);
        Assert.True(none["arxiv"] && none["openalex"] && none["semanticscholar"] && none["crossref"]);

        var withUserKeys = engine.GetSourceAvailability(new UserApiKeys(ElsevierApiKey: "abc", IeeeApiKey: "YOUR_IEEE_KEY"));
        Assert.True(withUserKeys["scopus"]);
        Assert.False(withUserKeys["ieee"]);

        Assert.True(new PrismaReviewEngine("m", "server-elsevier", "server-ieee").GetSourceAvailability(null)["ieee"]);
    }

    [Fact]
    public async Task AFinishedRunLeavesItsLedgerMetricsAndProtocolFingerprint()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv", "scopus" }, CitationChaining: false));

        string folder = Path.Join(_root, runId.ToString("N"));
        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        Assert.Equal(new[] { "arxiv", "scopus" }, state.SelectedSources);
        Assert.Equal(new[] { "scopus" }, state.UnavailableSources);
        // The fingerprint is of protocol.md as written before the search; the search strings are appended after it.
        Assert.Matches("^[0-9a-f]{64}$", state.ProtocolSha256);
        Assert.NotEqual(RunManifest.Sha256(File.ReadAllBytes(Path.Join(folder, "protocol.md"))), state.ProtocolSha256);
        Assert.NotNull(state.CompletedUtc);
        Assert.NotNull(state.RunSettings);
        Assert.True(File.Exists(Path.Join(folder, "llm-calls.json")));
        Assert.Equal(PrismaReviewEngine.StageComplete, engine.LoadHeader(runId)!.Stage);

        var metrics = JsonSerializer.Deserialize<RunMetrics>(File.ReadAllText(Path.Join(folder, "run-metrics.json")))!;
        Assert.Equal(new[]
        {
            "Synthesis.ThematicSynthesis", "Synthesis.DualCoding", "Synthesis.RepairCitations", "Synthesis.SecondCitationCheck", "Synthesis.MaxThemes",
            "DualScreening", "CitationChaining", "PeerReviewOnly",
        }, metrics.Settings.Keys);
        Assert.Equal(("True", "False", "False"), (metrics.Settings["DualScreening"], metrics.Settings["CitationChaining"], metrics.Settings["PeerReviewOnly"]));
        Assert.Equal(runId.ToString("N"), metrics.RunId);

        Assert.Null(engine.LoadState(Guid.NewGuid()));
        Assert.Equal(0, engine.MarkInterruptedRuns());
    }

    [Fact]
    public void ARunLeftWorkingByARestartIsMarkedInterrupted()
    {
        var engine = new PrismaReviewEngine("m", "", null, null, null, new RunsOptions()) { WorkspaceRoot = _root };
        var runId = Guid.NewGuid();
        string folder = Path.Join(_root, runId.ToString("N"));
        Directory.CreateDirectory(folder);
        var state = new ReviewState { Stats = new ReviewStats { ProcessingStage = PrismaReviewEngine.StageSynthesizing, QueuePosition = 2 } };
        File.WriteAllText(Path.Join(folder, "transparent-process.json"), JsonSerializer.Serialize(state));
        Directory.CreateDirectory(Path.Join(_root, "not-a-run"));

        Assert.Equal(1, engine.MarkInterruptedRuns());
        var marked = engine.LoadState(runId)!;
        Assert.Equal((PrismaReviewEngine.StageInterrupted, 0), (marked.Stats.ProcessingStage, marked.Stats.QueuePosition));
        Assert.Equal("The server restarted while this run was in progress, so it could not finish. Please start it again.", marked.FailureMessage);
        Assert.NotNull(marked.CompletedUtc);
        Assert.Equal(0, engine.MarkInterruptedRuns());
    }
}

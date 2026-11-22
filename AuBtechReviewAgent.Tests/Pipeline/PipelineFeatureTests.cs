using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// End-to-end checks of citation chaining, the decision cache, live progress, the models named in the methods and
/// deleting a run, with the same offline fakes as PipelineTests.
/// </summary>
public class PipelineFeatureTests : PipelineFeatureRun
{
    private class FakeGraph : ICitationGraph
    {
        public string SourceName => "OpenAlex";
        public List<string> Dois { get; } = new();
        public Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection)
        {
            lock (Dois) Dois.Add(doi);
            var backward = new List<AcademicPaper>
            {
                new("OPENALEX_W1", "Agent supervisors revisited", "Agent loops recover.", "Published: 2023", new() { "Ann" }, "Journal of Agents", "10.1/back"),
                new("OPENALEX_W2", "Agent loops for safe orchestration", "dup", "Published: 2025", new() { "Jane Doe" }, "Future Internet"), // duplicate by title
            };
            var forward = new List<AcademicPaper>
            {
                new("OPENALEX_W3", "Wheat irrigation", "Agronomy.", "Published: 2026", new() { "Bo" }, "Field Crops Research", "10.1/fwd"),
            };
            return Task.FromResult(new CitationNeighbours(backward, forward, new[] { "{\"id\":\"W0\"}" }));
        }
    }

    [Fact]
    public async Task CitationChainingAddsScreenedRecordsFromOtherMethods()
    {
        var graph = new FakeGraph();
        var engine = Engine(graph: graph);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request(chaining: true));

        var state = engine.LoadState(runId)!;
        Assert.Equal(new[] { "10.3390/fi1234567" }, graph.Dois); // only the included record with a DOI is chained
        Assert.Equal(3, state.Stats.IdentifiedViaCitations);
        Assert.Equal(6, state.Stats.TotalIdentified);
        Assert.Equal(1, state.Stats.DuplicatesRemoved);

        var chained = state.Phases.Screening.Where(l => l.Origin == PrismaReviewEngine.OriginCitations).ToList();
        Assert.Equal(2, chained.Count);
        Assert.Equal("Included", chained.Single(l => l.PaperId == "OPENALEX_W1").Decision);
        Assert.Equal("Excluded", chained.Single(l => l.PaperId == "OPENALEX_W3").Decision);
        Assert.True(PrismaFlowCounts.From(state).IsConsistent);
        Assert.Contains("citation chaining (n = 3)", PrismaFlowDiagram.ToTikz(PrismaFlowCounts.From(state)));
        Assert.Contains(state.SearchLogs, l => l.SourceName == "OpenAlex (citation chaining)" && l.RawResponseFile != null);

        string report = File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json"));
        Assert.Contains("citation chaining was run through OpenAlex", report);
    }

    [Fact]
    public async Task ASecondIdenticalRunReusesSearchesAndScreeningDecisions()
    {
        var cache = new ReviewCache(new CacheOptions { Folder = Path.Join(_root, "cache") }, _root);
        var source = new PipelineTests.FakeSource();
        var firstChat = new FakeChatService().RespondsWith(PipelineTests.Respond);
        await Engine(source: source, cache: cache, chat: firstChat).RunReviewAsync(Guid.NewGuid(), Request());
        int callsAfterFirst = source.Calls;

        var secondChat = new FakeChatService().RespondsWith(PipelineTests.Respond);
        var runId = Guid.NewGuid();
        var engine = Engine(source: source, cache: cache, chat: secondChat);
        await engine.RunReviewAsync(runId, Request());

        var state = engine.LoadState(runId)!;
        Assert.Equal(callsAfterFirst, source.Calls); // no new source queries
        Assert.DoesNotContain("Evaluate the following academic paper", string.Join("\n", secondChat.Prompts));
        Assert.True(state.Phases.Screening.All(l => l.FromCache));
        Assert.True(state.SearchLogs.All(l => l.FromCache));
        Assert.Equal(2, state.Stats.Included);
    }

    [Fact]
    public async Task APageOpenedWhileTheReviewIsWrittenSeesTheLiveStepNotTheLastSavedOne()
    {
        // Seen live: a page opened during the writing showed "Extraction and appraisal" for minutes, because the ledger is
        // not saved while the review is written and checked.
        var runId = Guid.NewGuid();
        PrismaReviewEngine? engine = null;
        string? liveStep = null, savedStep = null;
        string Respond(string prompt)
        {
            if (prompt.Contains("You are turning the results of a systematic literature review") && liveStep == null)
            {
                var live = engine!.LiveStats(runId)!;
                lock (live) liveStep = live.ProgressStep;
                savedStep = engine.LoadState(runId)!.Stats.ProgressStep;
            }
            return PipelineTests.Respond(prompt);
        }
        engine = Engine(Respond);
        await engine.RunReviewAsync(runId, Request());

        Assert.Equal(RunProgress.Synthesis, liveStep);
        Assert.NotEqual(RunProgress.Synthesis, savedStep); // what a page opened then used to show
        Assert.Null(engine.LiveStats(runId)); // forgotten once the run has ended
    }

    [Fact]
    public async Task ScreeningDecisionsAreReusedOnlyUnderTheModelThatMadeThem()
    {
        var cache = new ReviewCache(new CacheOptions { Folder = Path.Join(_root, "cache") }, _root);
        async Task<ReviewState> RunWith(string fastModel)
        {
            var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
            {
                WorkspaceRoot = _root,
                ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
                SourceFactory = _ => new PipelineTests.FakeSource(),
                Cache = cache,
                Llm = new LlmOptions { FastModel = fastModel },
                FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
            };
            var runId = Guid.NewGuid();
            await engine.RunReviewAsync(runId, Request());
            return engine.LoadState(runId)!;
        }

        await RunWith("mistral-small-latest");
        Assert.DoesNotContain((await RunWith("another-small-model")).Phases.Screening, l => l.FromCache);
        Assert.True((await RunWith("mistral-small-latest")).Phases.Screening.All(l => l.FromCache));
    }

    [Fact]
    public void TheMethodsTextNamesTheModelThatScreenedAndCheckedCitations()
    {
        var options = new LlmOptions { FastModel = "mistral-small-latest" };
        var stats = new ReviewStats { Screened = 10, Included = 4, Excluded = 6, DualScreened = 10, ScreeningKappa = 0.8 };

        string selection = MethodsSectionWriter.SelectionProcess(false, stats, modelName: options.DisplayNameFor("screening"));
        Assert.Contains("language model (mistral-small-latest, Mistral API, temperature 0)", selection);

        string synthesis = ThematicSynthesis.MethodsText(null, options.DisplayName, true, options.DisplayNameFor("citation-check"));
        Assert.Contains("synthesised narratively by the language model (mistral-large-latest, Mistral API)", synthesis);
        Assert.Contains("checked against the cited paper by a second language model (mistral-small-latest, Mistral API)", synthesis);

        // One model: the text is as before, with no second model named.
        string single = ThematicSynthesis.MethodsText(null, new LlmOptions().DisplayName, true, new LlmOptions().DisplayNameFor("citation-check"));
        Assert.DoesNotContain("second language model", single);

        var state = new ReviewState { RunSettings = new RunSettingsRecord { Model = "mistral-large-latest", FastModel = "mistral-small-latest" } };
        Assert.Contains("mistral-large-latest (and mistral-small-latest for screening and citation checks)", AiUseStatement.Methods(state, Guid.Empty));
        Assert.Contains("using mistral-large-latest and mistral-small-latest", AiUseStatement.Short(state));
    }

    [Fact]
    public async Task AFinishedRunCanBeDeleted()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request());

        Assert.NotNull(engine.ReadProtocol(runId));
        Assert.True(engine.DeleteRun(runId));
        Assert.Null(engine.LoadState(runId));
        Assert.False(engine.DeleteRun(runId));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal runner (MLR block H): a planned run goes from step to step without a click, the model's map is
/// fixed as proposed, a step that fails stops the run where it is, and running it again continues from that step.
/// The runs start at "Assessed" with a fake model; the steps before it have their own tests.
/// </summary>
public class MultivocalRunnerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    /// <summary>The writer of the last run made with one, for the archive and main.tex.</summary>
    private MultivocalWriter? _writer;

    private const string PageWords = "We keep a running summary of the conversation. Retrieval brings in only the documents the agent needs.";

    private const string Map = """
        {"attributes":[{"name":"Practice","question":"RQ1","description":"The practice described.","values":["Retrieval","Summarisation","Other"],"multiple":true,"open":false}]}
        """;

    private const string Extraction = """
        {"purpose":{"value":"Shows how a team manages agent context.","quote":"We keep a running summary of the conversation."},
         "attributes":[{"attribute":"Practice","values":[{"value":"Summarisation","quote":"We keep a running summary of the conversation."}]}]}
        """;

    private sealed class Web : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request.RequestUri!.AbsolutePath == "/post" ? HttpStatusCode.OK : HttpStatusCode.NotFound));

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Respond(HttpStatusCode status) =>
            new(status) { Content = new StringContent($"<html><body><main><p>{PageWords}</p></main></body></html>", Encoding.UTF8, "text/html") };
    }

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

    /// <summary>The fake model; while <paramref name="mapBroken"/> says so, its map answers are not valid.</summary>
    private static FakeChatService Model(Func<bool> mapBroken) => new FakeChatService().RespondsWith(prompt =>
        prompt.Contains("You build the systematic map") ? (mapBroken() ? "{}" : Map)
        : prompt.Contains("You extract data from one grey") ? Extraction
        : prompt.Contains("does each quote") ? "{\"verdicts\":[" + string.Join(",", Regex.Matches(prompt, @"^(F\d+\.\d+) \|", RegexOptions.Multiline)
            .Select(m => $$"""{"finding":"{{m.Groups[1].Value}}","verdict":"supported","reason":""}""")) + "]}"
        : prompt.Contains("Name one to") ? """{"themes":[{"name":"Summaries keep the context short","description":"Sources keep running summaries."}]}"""
        : prompt.Contains("VALUES TO PLACE") ? """{"placements":[{"group":"V1","themes":["Summaries keep the context short"],"reason":""}]}"""
        : WritingAnswer(prompt) ?? """{"tensions":[]}""");

    /// <summary>The fake model's answers for writing and checking the paper of the run's one source; null for any other prompt.</summary>
    private static string? WritingAnswer(string prompt) =>
        prompt.Contains("You are writing one subsection of the Results") ? """{"heading":"Summaries","text":"Sources keep a running summary of the conversation [1]."}"""
        : prompt.Contains("You are writing the Discussion section") ? """{"text":"Running summaries keep agent contexts short [1]."}"""
        : prompt.Contains("You are writing the summary for practitioners") ? """{"lead":"For developers.","items":["Keep a running summary [1].","Summarise old turns [1].","Keep the summary short [1]."]}"""
        : prompt.Contains("You are writing the title") ? """{"title":"Context engineering: a grey literature review","abstract":"One source keeps running summaries.","rationale":"Agents keep a running summary [1].","objectives":"To map the practices."}"""
        : prompt.Contains("grounded synthesis outline") || prompt.Contains("strict but constructive peer reviewer") || prompt.Contains("rigorous academic copyeditor") || prompt.Contains("You are checking citations")
            ? MultivocalWritingTests.Respond(prompt)
            : null;

    /// <summary>A run at "Assessed" with its page kept, and a runner over the module's own services.</summary>
    private async Task<(RunStore Store, Guid RunId, string Key, MultivocalRunner Runner)> AssessedRun(FakeChatService model, bool anyPassed = true, bool withWriter = false, bool recorded = false, RunMetricsStore? metrics = null)
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new Web(), _ => Task.CompletedTask));
        await pages.KeepAsync(runId, key, "https://blog.example.org/post");

        string folder = store.FolderOf(runId);
        File.WriteAllText(Path.Join(folder, MultivocalQualityAssessor.QualityFile), JsonSerializer.Serialize(new MultivocalQualityFile
        {
            RunId = runId,
            Threshold = 10,
            Sources = { new GreyQuality { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post", Points = 12, Outcome = anyPassed ? "Passed" : "Below threshold" } },
        }));
        File.WriteAllText(Path.Join(folder, MultivocalScreener.ScreeningFile), JsonSerializer.Serialize(new MultivocalScreeningFile
        {
            RunId = runId,
            Sources = { new GreyScreening { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post", Decision = "Included" } },
        }));
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalQualityAssessor.StageAssessed });

        // With recording, the steps use the run's recording service, as the server's factory does.
        Func<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService> chat = recorded ? () => LlmRecording.Current ?? (Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService)model : () => model;
        var screener = new MultivocalScreener(store, planner, searcher, chat);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, chat);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, chat);
        MultivocalWriter? writer = null;
        if (withWriter)
        {
            var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, assessor, mapper, extractor, synthesiser);
            writer = new MultivocalWriter(store, reporter, pages, chat, "fake-model", parallelism: 1, metrics: metrics);
            _writer = writer;
        }
        return (store, runId, key, new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages, writer,
            recorded ? () => model : null, "fake-model"));
    }

    [Fact]
    public async Task ARunGoesFromStepToStepToTheSynthesisWithoutAClick()
    {
        var (store, runId, key, runner) = await AssessedRun(Model(() => false));
        var steps = new List<string>();
        runner.Changed += id => { if (id == runId && runner.StatusOf(id) is { } s && (steps.Count == 0 || steps[^1] != s.Step)) steps.Add(s.Step); };

        Assert.False(runner.Start(runId, "wrong-key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(runId, "wrong-key"));
        await runner.RunAsync(runId, key);

        Assert.Equal(new[] { "Starting", "Proposing the map", "Fixing the map", "Extracting", "Synthesising", "Writing the paper", "Done" }, steps); // without a writer the run ends after the references
        string folder = store.FolderOf(runId);
        Assert.Contains("Source [1] in the TraceableAI review", File.ReadAllText(Path.Join(folder, GreyReferences.RisFile)));
        Assert.Contains("@misc{ana2025managing,", File.ReadAllText(Path.Join(folder, GreyReferences.BibFile)));
        Assert.Contains("KW  - included", File.ReadAllText(Path.Join(folder, GreyReferences.ScreenedFile)));
        Assert.Equal(MultivocalSynthesiser.StageSynthesised, store.LoadHeader(runId)!.Stage);
        var map = JsonSerializer.Deserialize<MultivocalMapFile>(File.ReadAllText(Path.Join(store.FolderOf(runId), MultivocalMapper.MapFile)))!;
        Assert.Equal(1, map.FixedVersion); // the model's proposal, fixed as it is
        var done = runner.StatusOf(runId)!;
        Assert.False(done.Running);
        Assert.Equal("Done", done.Step);
        Assert.Null(done.Error);
        Assert.True(done.StartedUtc <= done.EndedUtc); // the page shows the elapsed time
        Assert.True(MultivocalRunner.HasStepsLeft(store.LoadHeader(runId)!.Stage)); // writing and checking need the writer
    }

    [Fact]
    public async Task WithTheWriterTheRunGoesOnToWriteAndCheckThePaper()
    {
        var (store, runId, key, runner) = await AssessedRun(Model(() => false), withWriter: true);
        var steps = new List<string>();
        runner.Changed += id => { if (id == runId && runner.StatusOf(id) is { } s && (steps.Count == 0 || steps[^1] != s.Step)) steps.Add(s.Step); };

        await runner.RunAsync(runId, key);

        Assert.Equal(new[] { "Starting", "Proposing the map", "Fixing the map", "Extracting", "Synthesising", "Writing the paper", "Checking the citations", "Done" }, steps);
        Assert.Equal(MultivocalWriter.StageChecked, store.LoadHeader(runId)!.Stage);
        Assert.False(MultivocalRunner.HasStepsLeft(MultivocalWriter.StageChecked));
        string folder = store.FolderOf(runId);
        var paper = JsonSerializer.Deserialize<MultivocalPaperFile>(File.ReadAllText(Path.Join(folder, MultivocalWriter.PaperFile)))!;
        Assert.Equal("Context engineering: a grey literature review", paper.Title);
        Assert.Equal("Sources keep a running summary of the conversation [1].", paper.Results.Single(r => r.Kind == "theme").Text);
        Assert.True(paper.Checks!.Checked > 0);
        Assert.True(File.Exists(Path.Join(folder, MultivocalWriter.AuditFile)));
        Assert.True(File.Exists(Path.Join(folder, GreyReferences.BibFile))); // written before the paper, which cites by its numbers

        // The archive of a multivocal run: main.tex built from the checked paper, then the run's files and the manifest.
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(_writer!.Archive(runId)));
        Assert.Equal(MultivocalPaper.MainTexFile, archive.Entries[0].FullName);
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalWriter.PaperFile);
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalWriter.AuditFile);
        Assert.Contains(archive.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(@"\section{Summary for practitioners}", _writer.MainTex(runId));
        Assert.Empty(_writer.Archive(Guid.NewGuid())); // not a run
    }

    [Fact]
    public async Task AStepThatFailsStopsTheRunAndRunningItAgainContinuesFromThatStep()
    {
        bool broken = true;
        var (store, runId, key, runner) = await AssessedRun(Model(() => broken));

        await runner.RunAsync(runId, key);

        var stopped = runner.StatusOf(runId)!;
        Assert.False(stopped.Running);
        Assert.Equal("Proposing the map", stopped.Step);
        Assert.StartsWith("Proposing the map stopped:", stopped.Error);
        Assert.Equal(MultivocalQualityAssessor.StageAssessed, store.LoadHeader(runId)!.Stage); // nothing after the failed step ran

        broken = false;
        await runner.RunAsync(runId, key);
        Assert.Equal(MultivocalSynthesiser.StageSynthesised, store.LoadHeader(runId)!.Stage);
        Assert.Null(runner.StatusOf(runId)!.Error);
    }

    [Fact]
    public async Task EveryModelCallOfEveryStartIsAddedToTheRunsCallLog()
    {
        bool broken = true;
        var (store, runId, key, runner) = await AssessedRun(Model(() => broken), recorded: true);
        string folder = store.FolderOf(runId);

        await runner.RunAsync(runId, key); // stops at the map
        var first = MultivocalRunLog.LoadCalls(folder)!;
        Assert.NotEmpty(first.Calls);
        Assert.All(first.Calls, c => Assert.Equal("map", c.Stage));

        broken = false;
        await runner.RunAsync(runId, key);
        var log = MultivocalRunLog.LoadCalls(folder)!;
        Assert.Equal(first.Calls.Select(c => c.PromptSha256), log.Calls.Take(first.Calls.Count).Select(c => c.PromptSha256)); // the first start's calls are kept
        Assert.Equal(Enumerable.Range(1, log.Calls.Count), log.Calls.Select(c => c.Sequence)); // and the second start's are numbered on
        Assert.Contains(log.Calls, c => c.Stage == "extraction");
        Assert.Contains(log.Calls, c => c.Stage == "synthesis");
        Assert.Equal(log.Calls.Count, log.RunSettings!.LlmCalls);
        Assert.Equal("fake-model", log.RunSettings.Model);
        Assert.Null(LlmRecording.Current); // the scope ends with the run
    }

    [Fact]
    public async Task ACheckedRunWritesItsMetricsOnceAndKeepsThemApartFromTheSystematicRuns()
    {
        var metrics = new RunMetricsStore(new MetricsOptions { Folder = Path.Join(_root, "metrics") }, _root);
        var (store, runId, key, runner) = await AssessedRun(Model(() => false), withWriter: true, recorded: true, metrics: metrics);
        string folder = store.FolderOf(runId);

        await runner.RunAsync(runId, key);
        await runner.RunAsync(runId, key); // nothing is left to run, so nothing is added again

        var m = JsonSerializer.Deserialize<RunMetrics>(File.ReadAllText(Path.Join(folder, MultivocalRunLog.MetricsFile)))!;
        Assert.True(m.IsMultivocal);
        Assert.Equal("grey literature review", m.Settings["ReviewKind"]);
        Assert.Equal(1, m.Identified);
        Assert.Equal(1, m.Included); // kept for the synthesis
        Assert.Equal(1, m.Themes);
        Assert.Equal(1, m.StudiesCited);
        Assert.True(m.CitationsFinal.Total > 0);
        Assert.True(m.SynthesisWords > 0);
        var calls = MultivocalRunLog.LoadCalls(folder)!.Calls;
        Assert.Equal(calls.Count, m.LlmCalls);
        Assert.Contains("theme-sections", m.CostByStage.Keys); // the shared writing steps label their own calls
        Assert.Contains("citation-check", m.CostByStage.Keys);
        Assert.DoesNotContain("unlabelled", m.CostByStage.Keys);
        Assert.Equal(m.RunId, Assert.Single(metrics.ReadAll(multivocal: true)).RunId);
        Assert.Empty(metrics.ReadAll()); // the systematic review's figures stay its own

        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(_writer!.Archive(runId)));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalRunLog.CallsFile);
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalRunLog.MetricsFile);
    }

    [Fact]
    public void TheCallsOfAStartAreNumberedOnFromTheCallsAlreadyInTheLog()
    {
        var earlier = new List<LlmCallRecord> { new() { Sequence = 1, Stage = "screening" }, new() { Sequence = 2, Stage = "screening" } };
        var now = new List<LlmCallRecord> { new() { Sequence = 2, Stage = "map" }, new() { Sequence = 1, Stage = "map", PromptSha256 = "a" } };

        var merged = MultivocalRunLog.Merge(earlier, now);

        Assert.Equal(new[] { 1, 2, 3, 4 }, merged.Select(c => c.Sequence));
        Assert.Equal("a", merged[2].PromptSha256); // in the order they were made
        Assert.Equal(2, now[0].Sequence); // the recorder's own records are left as they are
        Assert.Empty(MultivocalRunLog.Merge(new List<LlmCallRecord>(), new List<LlmCallRecord>()));
    }

    [Fact]
    public async Task ARunWithNoSourceThatPassedStopsBeforeTheMap()
    {
        var (store, runId, key, runner) = await AssessedRun(Model(() => false), anyPassed: false);

        await runner.RunAsync(runId, key);

        var status = runner.StatusOf(runId)!;
        Assert.Equal("Stopped", status.Step);
        Assert.Contains("No source passed the quality check", status.Error);
        Assert.Equal(MultivocalQualityAssessor.StageAssessed, store.LoadHeader(runId)!.Stage);
    }

    [Fact]
    public void TheProgressFollowsTheStageAndTheProgressLine()
    {
        Assert.Equal(0, MultivocalProgress.Percent(MultivocalPlanner.StagePlanned, null));
        Assert.Equal(4, MultivocalProgress.Percent(MultivocalSearcher.StageSearching, "4 of 5 searches · Fake: \"context\"")); // four fifths of the 5 of the search
        Assert.Equal(5 + 20 + 25, MultivocalProgress.Percent(MultivocalQualityAssessor.StageAssessed, null));
        Assert.Equal(5 + 20 + 25 + 5 + 9, MultivocalProgress.Percent(MultivocalExtractor.StageExtracting, "18 of 30 sources extracted"));
        Assert.Equal(80, MultivocalProgress.Percent(MultivocalSynthesiser.StageSynthesised, null)); // writing and checking are left
        Assert.Equal(90 + 5, MultivocalProgress.Percent(MultivocalWriter.StageChecking, "First check: 5 of 10 sources"));
        Assert.Equal(100, MultivocalProgress.Percent(MultivocalWriter.StageChecked, null));
        Assert.Equal(0, MultivocalProgress.Fraction("RQ1: findings in 4 values; naming the themes"));
        Assert.Equal(new[] { "done", "done", "current", "waiting", "waiting", "waiting", "waiting", "waiting" },
            Enumerable.Range(0, MultivocalProgress.Steps.Count).Select(i => MultivocalProgress.StateOf(i, MultivocalQualityAssessor.StageAssessing)));
    }
}

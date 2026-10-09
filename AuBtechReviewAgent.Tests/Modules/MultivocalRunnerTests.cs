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
            Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/post" ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent($"<html><body><main><p>{PageWords}</p></main></body></html>", Encoding.UTF8, "text/html"),
            });
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
        : """{"tensions":[]}""");

    /// <summary>A run at "Assessed" with its page kept, and a runner over the module's own services.</summary>
    private async Task<(RunStore Store, Guid RunId, string Key, MultivocalRunner Runner)> AssessedRun(FakeChatService model, bool anyPassed = true)
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
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalQualityAssessor.StageAssessed });

        var screener = new MultivocalScreener(store, planner, searcher, () => model);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, () => model);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, () => model, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, () => model);
        return (store, runId, key, new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser));
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

        Assert.Equal(new[] { "Starting", "Proposing the map", "Fixing the map", "Extracting", "Synthesising", "Done" }, steps);
        Assert.Equal(MultivocalSynthesiser.StageSynthesised, store.LoadHeader(runId)!.Stage);
        var map = JsonSerializer.Deserialize<MultivocalMapFile>(File.ReadAllText(Path.Join(store.FolderOf(runId), MultivocalMapper.MapFile)))!;
        Assert.Equal(1, map.FixedVersion); // the model's proposal, fixed as it is
        var done = runner.StatusOf(runId)!;
        Assert.Equal((false, "Done", (string?)null), (done.Running, done.Step, done.Error));
        Assert.True(done.StartedUtc <= done.EndedUtc); // the page shows the elapsed time
        Assert.False(MultivocalRunner.HasStepsLeft(store.LoadHeader(runId)!.Stage));
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
        Assert.Equal(5 + 25 + 35, MultivocalProgress.Percent(MultivocalQualityAssessor.StageAssessed, null));
        Assert.Equal(5 + 25 + 35 + 5 + 10, MultivocalProgress.Percent(MultivocalExtractor.StageExtracting, "15 of 30 sources extracted"));
        Assert.Equal(100, MultivocalProgress.Percent(MultivocalSynthesiser.StageSynthesised, null));
        Assert.Equal(0, MultivocalProgress.Fraction("RQ1: findings in 4 values; naming the themes"));
        Assert.Equal(new[] { "done", "done", "current", "waiting", "waiting", "waiting" },
            Enumerable.Range(0, MultivocalProgress.Steps.Count).Select(i => MultivocalProgress.StateOf(i, MultivocalQualityAssessor.StageAssessing)));
    }
}

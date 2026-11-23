using AuBtechReviewAgent;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole grey literature run through the runner with a chat service routed by stage, as the server makes it when
/// Llm:FastModel is set: the call log records which model each stage's calls went to, and only the narrow checks
/// (both screenings and the citation checks) go to the fast model.
/// </summary>
public class MultivocalRoutingRunnerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrroute-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task TheCallLogRecordsTheFastModelForTheChecksAndTheMainModelForTheRest()
    {
        var options = MultivocalFastModelTests.Options();
        var (main, fast) = (new FakeChatService().RespondsWith(ModuleKit.RespondMultivocal), new FakeChatService().RespondsWith(ModuleKit.RespondMultivocal));
        var routed = new StageRoutedChatService(main, fast, options);
        IChatCompletionService Chat() => LlmRecording.Current ?? (IChatCompletionService)routed;

        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example("grey");
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new ModuleKit.KitGreySource());
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new ModuleKit.KitWeb(), _ => Task.CompletedTask));
        var screener = new MultivocalScreener(store, planner, searcher, Chat, model: options.ModelFor("screening"), parallelism: 1, modelFor: options.ModelFor);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, Chat, parallelism: 1);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, Chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, Chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, Chat);
        var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, assessor, mapper, extractor, synthesiser);
        var writer = new MultivocalWriter(store, reporter, pages, Chat, options.Model, parallelism: 1, checkModel: options.ModelFor("citation-check"));
        var runner = new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages, writer, () => routed, options.Model);

        await runner.RunAsync(runId, key);
        Assert.Null(runner.StatusOf(runId)?.Error);

        var calls = MultivocalRunLog.LoadCalls(store.FolderOf(runId))!.Calls;
        string ModelOf(string stage) => Assert.Single(calls.Where(c => c.Stage == stage).Select(c => c.ModelRequested).Distinct());
        Assert.Equal("small-model", ModelOf("screening"));
        Assert.Equal("small-model", ModelOf("screening-second"));
        Assert.Equal("small-model", ModelOf("citation-check"));
        Assert.Equal("large-model", ModelOf("extraction"));
        Assert.Equal("large-model", ModelOf("discussion"));
        Assert.Equal("large-model", ModelOf("quality"));
        Assert.All(calls.Where(c => !LlmOptions.FastStages.Contains(c.Stage)), c => Assert.Equal("large-model", c.ModelRequested));

        // The routing sent each call where the log says it went, and the run's files name the models for the statement on AI use.
        Assert.Equal(calls.Count(c => c.ModelRequested == "small-model"), fast.Prompts.Count);
        Assert.Equal(calls.Count(c => c.ModelRequested == "large-model"), main.Prompts.Count);
        Assert.Equal("small-model", screener.Load(runId)!.Model);
        Assert.Equal("small-model", writer.Load(runId)!.CheckModel);
        Assert.Contains("Of these, small-model answered both screenings of every source and the citation checks, and large-model did every other step that used a model.",
            MultivocalPaper.AiUse(reporter.LoadInput(runId)!, writer.Load(runId)!));
    }
}

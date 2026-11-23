using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal review's narrow, high-volume calls go to the fast model (Llm:FastModel), as in the systematic review:
/// both screenings are labelled with the systematic review's stages, so a chat service routed by stage sends them to
/// the fast model, and their cache keys and the screening file name the model that answered.
/// </summary>
public class MultivocalFastModelTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrfast-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            new("x:1", "Memory for LLM agents", "About memory.", "https://a.example.org/1", "Ana", "a.example.org", "blogs", null, new Dictionary<string, long>()),
            new("x:2", "Baking sourdough bread", "About bread.", "https://b.example.org/2", "Bo", "b.example.org", "blogs", null, new Dictionary<string, long>()),
        });
    }

    private static FakeChatService Screener() => new FakeChatService().RespondsWith(_ =>
        """{"decision":"Included","reasoning":"On topic.","briefSummary":"A post.","confidence":"high"}""");

    public static LlmOptions Options() => new() { Model = "large-model", FastModel = "small-model" };

    private async Task<(Guid RunId, string Key, MultivocalPlanner Planner, MultivocalSearcher Searcher)> SearchedRun(RunStore store)
    {
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        plan.FormalDatabases = new List<string>();
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);
        return (runId, key, planner, searcher);
    }

    [Fact]
    public async Task BothScreeningsGoToTheFastModelOutsideTheRunnerAndTheFileNamesIt()
    {
        var store = new RunStore(_root);
        var (runId, key, planner, searcher) = await SearchedRun(store);
        var (main, fast) = (Screener(), Screener());
        var options = Options();
        var routed = new StageRoutedChatService(main, fast, options);
        var screener = new MultivocalScreener(store, planner, searcher, () => routed, model: options.ModelFor("screening"), parallelism: 1, modelFor: options.ModelFor);

        var file = await screener.ScreenAsync(runId, key);

        Assert.Empty(main.Prompts);
        Assert.Equal(4, fast.Prompts.Count);
        Assert.Equal(2, fast.Prompts.Count(p => p.Contains("SECOND, INDEPENDENT")));
        Assert.Equal("small-model", file.Model);
    }

    [Fact]
    public async Task ADecisionOfTheMainModelIsNotReusedAsTheFastModels()
    {
        var store = new RunStore(_root);
        var cache = new ReviewCache(new CacheOptions { Folder = Path.Join(_root, "cache") }, _root);
        var (runId, key, planner, searcher) = await SearchedRun(store);
        var large = Screener();
        await new MultivocalScreener(store, planner, searcher, () => large, cache, "large-model", 1).ScreenAsync(runId, key);
        Assert.Equal(4, large.Prompts.Count);

        // The same run screened again: the same model takes its answers from the cache, the fast model asks again.
        var (secondRun, secondKey, _, _) = await SearchedRun(store);
        var again = Screener();
        var same = await new MultivocalScreener(store, planner, searcher, () => again, cache, "large-model", 1).ScreenAsync(secondRun, secondKey);
        Assert.Empty(again.Prompts);
        Assert.All(same.Sources, s => Assert.True(s.FromCache));

        var (thirdRun, thirdKey, _, _) = await SearchedRun(store);
        var small = Screener();
        var options = Options();
        var routed = await new MultivocalScreener(store, planner, searcher, () => small, cache, options.Model, 1, options.ModelFor).ScreenAsync(thirdRun, thirdKey);
        Assert.Equal(4, small.Prompts.Count);
        Assert.All(routed.Sources, s => Assert.False(s.FromCache));
    }

    [Fact]
    public async Task TheScreeningsAreLabelledWithTheSystematicReviewsStages()
    {
        var store = new RunStore(_root);
        var (runId, key, planner, searcher) = await SearchedRun(store);
        var stages = new List<string>();
        var chat = new FakeChatService().RespondsWith(_ =>
        {
            lock (stages) stages.Add(LlmStage.Name);
            return """{"decision":"Included","reasoning":"On topic.","briefSummary":"A post.","confidence":"high"}""";
        });

        await new MultivocalScreener(store, planner, searcher, () => chat, parallelism: 1).ScreenAsync(runId, key);

        Assert.Equal(new[] { "screening", "screening", "screening-second", "screening-second" }, stages.Order(StringComparer.Ordinal));
        Assert.True(LlmOptions.FastStages.Contains(MultivocalScreener.FirstStage) && LlmOptions.FastStages.Contains(MultivocalScreener.SecondStage));
    }
}

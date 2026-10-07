using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Screening the grey sources of a multivocal run (MLR block E, G9–G10), with a fake model: every source twice with
/// the plan's criteria and the systematic review's steps, disagreements kept and flagged, the same source under two
/// addresses screened once.
/// </summary>
public class MultivocalScreeningTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrscreen-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class Source : IGreySource
    {
        private readonly List<GreyRecord> _records;
        public Source(params (string Url, string Title, string Producer)[] records) => _records = records
            .Select(r => new GreyRecord($"x:{r.Url}", r.Title, $"About {r.Title}.", r.Url, r.Producer, new Uri(r.Url).Host, "blogs", null, new Dictionary<string, long>()))
            .ToList();
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(_records);
    }

    private static string Answer(bool include) => include
        ? """{"decision":"Included","reasoning":"On context engineering.","briefSummary":"A post.","confidence":"high"}"""
        : """{"decision":"Excluded","reasoning":"About something else.","briefSummary":"A post.","confidence":"high","exclusionReason":"off-topic"}""";

    /// <summary>Includes titles with "agent" in them; the second screener also includes "Disputed".</summary>
    private static FakeChatService Model() => new FakeChatService().RespondsWith(prompt =>
    {
        bool second = prompt.Contains("SECOND, INDEPENDENT");
        string title = prompt.Split("- Title: ")[1].Split('\n')[0];
        if (title.Contains("Broken")) throw new HttpRequestException("api.mistral.ai answered 503");
        return Answer(title.Contains("agent", StringComparison.OrdinalIgnoreCase) || (second && title.Contains("Disputed")));
    });

    private async Task<(RunStore Store, Guid RunId, string Key, MultivocalScreener Screener, FakeChatService Chat)> SearchedRun(
        MultivocalPlan plan, params (string, string, string)[] records)
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source(records));
        await searcher.SearchAsync(runId, key);
        var chat = Model();
        return (store, runId, key, new MultivocalScreener(store, planner, searcher, () => chat, model: "fake-model", parallelism: 1), chat);
    }

    [Fact]
    public async Task EverySourceIsScreenedTwiceWithThePlansCriteriaAndTheSharedSteps()
    {
        var (store, runId, key, screener, chat) = await SearchedRun(MultivocalPlan.Example(),
            ("https://a.example.org/1", "Memory for LLM agents", "Ana"),
            ("https://b.example.org/2", "Baking sourdough bread", "Bo"));

        var file = await screener.ScreenAsync(runId, key);

        Assert.Equal(4, chat.Prompts.Count);
        Assert.All(chat.Prompts, p => Assert.Contains(MultivocalPlan.Example().InclusionCriteria, p));
        Assert.All(chat.Prompts, p => Assert.Contains("Being grey literature, informal or not peer-reviewed is not a reason to exclude it", p));
        Assert.Equal(2, chat.Prompts.Count(p => p.Contains("SECOND, INDEPENDENT")));
        Assert.All(chat.Prompts, p => Assert.Contains(PrismaReviewEngine.ScreeningAnswerFormat.Trim(), p));

        Assert.Equal(new[] { "Included", "Excluded" }, file.Sources.Select(s => s.Decision));
        Assert.Equal("off-topic", file.Sources[1].ExclusionReason);
        Assert.Equal(1.0, file.Kappa);
        Assert.Equal(MultivocalScreener.StageScreened, store.LoadHeader(runId)!.Stage);
        Assert.Equal(2, screener.Load(runId)!.Sources.Count);

        var zip = RunArchive.Build(runId, store.FolderOf(runId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalScreener.ScreeningFile);
    }

    [Fact]
    public async Task ADisagreementKeepsTheSourceAndAFailedCallDoesNotStopTheOthers()
    {
        var (_, runId, key, screener, _) = await SearchedRun(MultivocalPlan.Example(),
            ("https://a.example.org/1", "Disputed post", "Ana"),
            ("https://b.example.org/2", "Broken record", "Bo"),
            ("https://c.example.org/3", "Tools for agents", "Cy"));

        var file = await screener.ScreenAsync(runId, key);

        var disputed = file.Sources[0];
        Assert.Equal("Included", disputed.Decision);
        Assert.True(disputed.Uncertain);
        Assert.Contains("disagreed (first: Excluded, second: Included)", disputed.Reasoning);
        Assert.Equal("Error", file.Sources[1].Decision);
        Assert.Contains("503", file.Sources[1].Reasoning);
        Assert.Equal("Included", file.Sources[2].Decision);
    }

    [Fact]
    public async Task TheSameTitleByTheSameProducerIsScreenedOnceButTheSameTitleByAnotherIsNot()
    {
        var (_, runId, key, screener, chat) = await SearchedRun(MultivocalPlan.Example(),
            ("https://doi.org/10.5281/zenodo.1", "Context engineering for agents", "Lee, K."),
            ("https://doi.org/10.5281/zenodo.2", "Context Engineering for Agents.", "Lee, K."), // another version's DOI
            ("https://other.example.org/post", "Context engineering for agents", "Someone else"));

        var file = await screener.ScreenAsync(runId, key);

        Assert.Equal(new[] { "Included", "Duplicate", "Included" }, file.Sources.Select(s => s.Decision));
        Assert.Equal("doi.org/10.5281/zenodo.1", file.Sources[1].DuplicateOf);
        Assert.Equal(4, chat.Prompts.Count);
    }

    [Fact]
    public async Task OnlyASearchedRunWithCriteriaCanBeScreenedByThePlannerAndOnlyOnce()
    {
        var (store, runId, key, screener, _) = await SearchedRun(MultivocalPlan.Example(), ("https://a.example.org/1", "Memory for agents", "Ana"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => screener.ScreenAsync(runId, "wrong-key"));
        Assert.Equal(MultivocalSearcher.StageSearched, store.LoadHeader(runId)!.Stage);
        await screener.ScreenAsync(runId, key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => screener.ScreenAsync(runId, key));

        // A plan saved before the form asked for criteria.
        var planner = new MultivocalPlanner(store);
        var old = MultivocalPlan.Example();
        var (oldRun, oldKey) = await planner.CreateAsync(old);
        string planFile = Path.Join(store.FolderOf(oldRun), MultivocalPlanner.PlanFile);
        File.WriteAllText(planFile, File.ReadAllText(planFile).Replace(old.InclusionCriteria, ""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => screener.ScreenAsync(oldRun, oldKey));
        Assert.Contains("no inclusion criteria", ex.Message);
    }
}

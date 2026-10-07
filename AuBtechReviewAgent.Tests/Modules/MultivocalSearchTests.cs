using System.Security.Cryptography;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Running the grey literature searches of a planned multivocal run (MLR block D), with fake sources.</summary>
public class MultivocalSearchTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrsearch-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    /// <summary>A grey source that answers from a script: one list of records per search string.</summary>
    private sealed class FakeGreySource : IGreySource
    {
        private readonly Func<string, int, List<GreyRecord>> _answer;
        private readonly List<string> _raw = new();
        public List<(string Query, int Max)> Calls { get; } = new();

        public FakeGreySource(string key, Func<string, int, List<GreyRecord>> answer) { Key = key; _answer = answer; }

        public string Key { get; }
        public string Name => $"Fake {Key}";
        public IReadOnlyList<string> LastRawResponses => _raw;

        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults)
        {
            Calls.Add((query, maxResults));
            _raw.Clear();
            _raw.Add($"{{\"query\":\"{query}\"}}");
            return Task.FromResult(_answer(query, maxResults));
        }
    }

    private static GreyRecord Record(string url, string title = "A source") =>
        new($"x:{url}", title, "summary", url, "someone", new Uri(url).Host, "blogs", null, new Dictionary<string, long>());

    private static MultivocalPlan Plan(params string[] searches)
    {
        var plan = MultivocalPlan.Example();
        plan.GreySearches = searches.ToList();
        plan.GreySearchStrings = new List<string> { "context engineering", "agent memory" };
        return plan;
    }

    private (RunStore Store, MultivocalPlanner Planner) Services()
    {
        var store = new RunStore(_root);
        return (store, new MultivocalPlanner(store));
    }

    [Fact]
    public async Task EverySearchIsOnFileWithItsRawAnswerAndTheSourcesAreFoundOnce()
    {
        var (store, planner) = Services();
        var (runId, key) = await planner.CreateAsync(Plan("stackexchange", "github", "backlinks"));
        var se = new FakeGreySource("stackexchange", (q, _) => q == "context engineering"
            ? new() { Record("https://stackoverflow.com/q/1"), Record("https://www.example.org/post/") }
            : new() { Record("https://stackoverflow.com/q/1") });
        var gh = new FakeGreySource("github", (_, _) => new() { Record("https://example.org/post") }); // the same page as above
        var searcher = new MultivocalSearcher(store, planner, k => k switch { "stackexchange" => se, "github" => gh, _ => null });

        var ledger = await searcher.SearchAsync(runId, key);

        Assert.Equal(4, ledger.Searches.Count); // 2 sources × 2 strings
        Assert.Equal(new[] { 2, 0, 0, 0 }, ledger.Searches.Select(s => s.New));
        Assert.Equal(2, ledger.Sources.Count);
        Assert.Equal(new[] { "stackexchange: context engineering", "github: context engineering", "github: agent memory" },
            ledger.Sources.Single(s => s.Record.Url.Contains("example.org")).FoundBy);
        Assert.Equal("backlinks", Assert.Single(ledger.Skipped).Source);

        string folder = store.FolderOf(runId);
        foreach (var search in ledger.Searches)
        {
            byte[] raw = File.ReadAllBytes(Path.Join(folder, search.RawFile));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), search.RawSha256);
        }
        Assert.Equal(MultivocalSearcher.StageSearched, store.LoadHeader(runId)!.Stage);
        Assert.Equal(2, searcher.LoadLedger(runId)!.Sources.Count);
    }

    [Fact]
    public async Task TheStoppingRuleDecidesHowManyHitsAreAskedFor()
    {
        var (store, planner) = Services();
        var plan = Plan("stackexchange");
        plan.TopHits = 300;
        var (runId, key) = await planner.CreateAsync(plan);
        var se = new FakeGreySource("stackexchange", (_, _) => new());

        var ledger = await new MultivocalSearcher(store, planner, _ => se).SearchAsync(runId, key);

        Assert.All(se.Calls, c => Assert.Equal(MultivocalSearcher.MaxPerSearch, c.Max)); // one page of at most 100
        Assert.Contains("the plan asked for 300", ledger.StoppingApplied);
    }

    [Fact]
    public async Task SaturationStopsASearchOnceAStringAddsNothingNew()
    {
        var (store, planner) = Services();
        var plan = Plan("stackexchange");
        plan.StoppingRule = "saturation";
        plan.GreySearchStrings = new List<string> { "a", "b", "c" };
        var (runId, key) = await planner.CreateAsync(plan);
        var se = new FakeGreySource("stackexchange", (_, _) => new() { Record("https://stackoverflow.com/q/1") });

        var ledger = await new MultivocalSearcher(store, planner, _ => se).SearchAsync(runId, key);

        Assert.Equal(new[] { "a", "b" }, se.Calls.Select(c => c.Query)); // "b" added nothing, so "c" was not searched
        Assert.Contains("saturation", ledger.StoppingApplied, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailingSearchIsRecordedAndTheOthersStillRun()
    {
        var (store, planner) = Services();
        var (runId, key) = await planner.CreateAsync(Plan("stackexchange", "github"));
        var failing = new FakeGreySource("stackexchange", (_, _) => throw new HttpRequestException("api.stackexchange.com answered 502"));
        var gh = new FakeGreySource("github", (_, _) => new() { Record("https://github.com/acme/kit") });

        var ledger = await new MultivocalSearcher(store, planner, k => k == "github" ? gh : failing).SearchAsync(runId, key);

        Assert.All(ledger.Searches.Where(s => s.Source == "stackexchange"), s => Assert.Contains("502", s.Error));
        Assert.Single(ledger.Sources);
    }

    [Fact]
    public async Task OnlyTheBrowserThatPlannedTheRunCanSearchItAndOnlyOnce()
    {
        var (store, planner) = Services();
        var (runId, key) = await planner.CreateAsync(Plan("stackexchange"));
        var searcher = new MultivocalSearcher(store, planner, _ => new FakeGreySource("stackexchange", (_, _) => new()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => searcher.SearchAsync(runId, "wrong-key"));
        Assert.Equal(MultivocalPlanner.StagePlanned, store.LoadHeader(runId)!.Stage);

        await searcher.SearchAsync(runId, key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => searcher.SearchAsync(runId, key));
    }

    [Fact]
    public async Task TheLedgerAndRawAnswersAreInTheArchive()
    {
        var (store, planner) = Services();
        var (runId, key) = await planner.CreateAsync(Plan("stackexchange"));
        await new MultivocalSearcher(store, planner, _ => new FakeGreySource("stackexchange", (_, _) => new() { Record("https://stackoverflow.com/q/1") }))
            .SearchAsync(runId, key);

        var zip = RunArchive.Build(runId, store.FolderOf(runId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(MultivocalSearcher.LedgerFile, names);
        Assert.Contains($"{RunArchive.RawResponsesFolder}/grey-stackexchange-1.json", names);
    }

    [Theory]
    [InlineData("https://www.example.org/post/", "example.org/post")]
    [InlineData("http://Example.org/post#top", "example.org/post")]
    [InlineData("https://example.org/post?id=2", "example.org/post?id=2")]
    public void TheSameSourceIsRecognisedWhateverTheAddressLooksLike(string url, string key) =>
        Assert.Equal(key, MultivocalSearcher.AddressKey(url));

    [Fact]
    public void ThePlansSearchStringsGoIntoTheProtocolAndTheTopicIsTheDefault()
    {
        var plan = MultivocalPlan.Example();
        string protocol = MultivocalProtocol.Write(plan, Guid.NewGuid(), DateTime.UtcNow);
        Assert.Contains("- LLM agent context window", protocol);

        plan.GreySearchStrings.Clear();
        Assert.Equal(new[] { plan.Topic }, plan.EffectiveGreySearchStrings);
        Assert.Contains("Search string for grey literature (G7), the topic:", MultivocalProtocol.Write(plan, Guid.NewGuid(), DateTime.UtcNow));

        plan.GreySearchStrings = Enumerable.Range(1, 6).Select(i => $"s{i}").ToList();
        Assert.Contains("Use at most 5 search strings.", plan.Problems());
    }
}

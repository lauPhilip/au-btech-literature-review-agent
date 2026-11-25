using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The seed step of a multivocal review's formal pool: after screening, the included academic sources with the most
/// citations per year are built out through a fake citation graph; what they find joins the formal pool with the same
/// duplicate checks, is screened like any academic source, is counted in the academic column and the method says so.
/// </summary>
public class MultivocalSeedChainingTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrseeds-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static AcademicPaper P(string id, string title, int year, int? cites, string? doi = null) =>
        new(id, title, $"An abstract about {title}.", $"Published: {year}", new() { "Ana" }, "Journal of Agents", doi, $"https://openalex.org/{id}", CitedByCount: cites);

    private sealed class Database : IAcademicSource
    {
        public string SourceName => "OpenAlex";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5) => Task.FromResult(new List<AcademicPaper>
        {
            P("W_old", "Agent memory classics", 2006, 400, "10.5555/old"),   // 19 a year
            P("W_new", "Agent context windows", 2025, 300, "10.5555/new"),   // 150 a year in 2026
            P("W_off", "Wheat rust in Denmark", 2025, 9000, "10.5555/off"),  // most cited, but excluded
        });
    }

    private sealed class Graph : ICitationGraph
    {
        public List<string> Asked { get; } = new();
        public string SourceName => "OpenAlex";
        public Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection) => throw new InvalidOperationException("seeds use the pool");
        public Task<CitationNeighbours> GetCitationPoolAsync(string doi, int poolPerDirection)
        {
            Asked.Add(doi);
            return Task.FromResult(new CitationNeighbours(
                new() { P("W_low", "Agent benchmarks", 2010, 17, "10.5555/low"), P("W_ref", "Agent evaluation methods", 2024, 30, "10.5555/ref"), P("W_dup", "Agent memory classics", 2006, 400, "10.5555/old") },
                new() { P("W_cite", "Wheat yields and agents", 2026, 40, "10.5555/cite"), P("W_tiny", "Agent surveys", 2026, 1, "10.5555/tiny") },
                new[] { "{\"seed\":\"pool\"}" }));
        }
    }

    /// <summary>Includes titles that start with "Agent"; excludes the rest.</summary>
    private static FakeChatService Model() => new FakeChatService().RespondsWith(prompt =>
    {
        string title = prompt.Contains("- Title: ") ? prompt.Split("- Title: ")[1].Split('\n')[0] : "";
        return title.StartsWith("Agent", StringComparison.Ordinal)
            ? """{"decision":"Included","reasoning":"On agents.","briefSummary":"A paper.","confidence":"high"}"""
            : """{"decision":"Excluded","reasoning":"Off topic.","briefSummary":"A paper.","confidence":"high","exclusionReason":"off-topic"}""";
    });

    [Fact]
    public async Task TheMostCitedIncludedAcademicSourceIsBuiltOutAndWhatItFindsIsScreenedAndCounted()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store) { SeedStudies = 1, SeedChainingPerDirection = 2 };
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        plan.FormalDatabases = new List<string> { "openalex" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new ModuleKit.KitGreySource(), _ => new Database());
        await searcher.SearchAsync(runId, key);
        var graph = new Graph();
        var chat = Model();
        var screener = new MultivocalScreener(store, planner, searcher, () => chat, model: "fake-model", parallelism: 1)
        {
            CitationGraph = () => graph, SeedStudies = 1, SeedChainingPerDirection = 2,
        };

        var file = await screener.ScreenAsync(runId, key);
        var ledger = searcher.LoadLedger(runId)!;

        // The newer study wins per year; the excluded paper with the most citations is never a seed.
        var seed = Assert.Single(ledger.SeedChaining!.Seeds);
        Assert.Equal(("W_new", 300), (seed.PaperId, seed.CitedByCount));
        Assert.Equal(new[] { "10.5555/new" }, graph.Asked);

        // Two per direction, by citations per year: W_ref and W_dup backward (W_dup a duplicate of a found source), W_cite and W_tiny forward.
        Assert.Equal((2, 2, 4), (seed.Backward, seed.Forward, ledger.SeedChaining.Identified));
        var removed = Assert.Single(ledger.FormalRemoved!, r => r.Source == MultivocalScreener.SeedChainingName);
        Assert.Equal(RemovedRecord.Duplicate, removed.Reason);
        var fromSeed = ledger.FormalPool.Where(s => s.FoundBy.All(f => f.StartsWith("seed chaining:", StringComparison.Ordinal))).Select(s => s.Paper!.Id).ToList();
        Assert.Contains(ledger.FormalPool.Single(s => s.Paper!.Id == "W_old").FoundBy, f => f.StartsWith("seed chaining: seed 1", StringComparison.Ordinal));
        Assert.Equal(new[] { "W_ref", "W_cite", "W_tiny" }, fromSeed);

        // Screened with the formal screening and entered in the file: two included, one excluded.
        var entries = file.Sources.Where(e => fromSeed.Select(id => ledger.FormalPool.First(s => s.Paper!.Id == id).Record.Url)
            .Select(MultivocalSearcher.AddressKey).Contains(e.Address)).ToList();
        Assert.All(entries, e => Assert.True(e.Formal));
        Assert.Equal(new[] { "Included", "Excluded", "Included" }, entries.Select(e => e.Decision));
        Assert.Equal(file.Sources.Count, screener.Load(runId)!.Sources.Count);

        // The raw answer is on file with its fingerprint.
        var log = Assert.Single(ledger.SeedSearches!);
        Assert.Equal(RunManifest.Sha256(File.ReadAllBytes(Path.Join(store.FolderOf(runId), log.RawFile))), log.RawSha256);

        // Counted in the academic column: the four records the seed found are part of what the formal pool identified.
        var formal = MultivocalPools.For(true, ledger, file, null, null);
        Assert.Equal(4, formal.ViaSeeds);
        Assert.Equal(ledger.FormalPool.Count() + ledger.FormalRemoved!.Count, formal.Identified);
        Assert.Equal(0, MultivocalPools.For(false, ledger, file, null, null).ViaSeeds);

        var method = string.Join(" ", MultivocalFormalText.SeedBlocks(ledger).Select(b => b.Text));
        Assert.Contains("was taken as the seed study", method);
        Assert.Contains("not by a language model", method);
        Assert.Contains("3 new academic sources joined the formal pool", method);
        Assert.Contains("Citation chaining from seed studies", File.ReadAllText(Path.Join(store.FolderOf(runId), RunStore.ProtocolFile)));
    }

    [Fact]
    public async Task WithoutACitationGraphTheScreeningIsAsBefore()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.FormalDatabases = new List<string> { "openalex" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new ModuleKit.KitGreySource(), _ => new Database());
        await searcher.SearchAsync(runId, key);
        var chat = Model();
        await new MultivocalScreener(store, planner, searcher, () => chat, model: "fake-model", parallelism: 1).ScreenAsync(runId, key);

        var ledger = searcher.LoadLedger(runId)!;
        Assert.Null(ledger.SeedChaining);
        Assert.Null(ledger.SeedSearches);
        Assert.Empty(MultivocalFormalText.SeedBlocks(ledger));
        Assert.DoesNotContain("Citation chaining from seed studies", File.ReadAllText(Path.Join(store.FolderOf(runId), RunStore.ProtocolFile)));
    }
}

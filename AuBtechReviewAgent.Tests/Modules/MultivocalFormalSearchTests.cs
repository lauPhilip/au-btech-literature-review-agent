using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Searching the formal pool of a multivocal review (block I-2): the academic databases with the formal strings.</summary>
public class MultivocalFormalSearchTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrformal-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class Failing : IAcademicSource
    {
        public string SourceName => "Broken";
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5) =>
            throw new HttpRequestException("GET https://api.example.org/search?apiKey=abcdefghijklmnopqrstuvwxyz0123 answered 500");
    }

    private sealed class Xml : IAcademicSource
    {
        public string SourceName => "arXiv";
        public IReadOnlyList<string> LastRawResponses => new[] { "<feed>one</feed>", "<feed>two</feed>" };
        public List<int> Asked { get; } = new();
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
        {
            Asked.Add(maxResults);
            return Task.FromResult(new List<AcademicPaper>
            {
                new("arxiv:1", "Context summaries for LLM agents", "An abstract.", "2025-01-02", new() { "Ana" }, "arXiv", null, "https://arxiv.org/abs/1"),
            });
        }
    }

    private async Task<(RunStore Store, Guid RunId, MultivocalLedger Ledger)> Search(Func<string, IAcademicSource?> academic, Action<MultivocalPlan>? change = null)
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        plan.FormalDatabases = new List<string> { "arxiv", "openalex", "scopus" };
        plan.FormalSearchStrings = new List<string> { "context AND agents", "agent memory" };
        change?.Invoke(plan);
        var (runId, key) = await planner.CreateAsync(plan);
        var ledger = await new MultivocalSearcher(store, planner, _ => new ModuleKit.KitGreySource(), academic).SearchAsync(runId, key);
        return (store, runId, ledger);
    }

    [Fact]
    public async Task EachRawAnswerIsKeptAsReceivedWithItsFingerprint()
    {
        var xml = new Xml();
        var (store, runId, ledger) = await Search(k => k == "arxiv" ? xml : null);

        var log = ledger.Searches.First(s => s.Formal);
        Assert.Equal("SourceResponses/formal-arxiv-1-1.xml; SourceResponses/formal-arxiv-1-2.xml", log.RawFile);
        var files = log.RawFile.Split("; ");
        var hashes = log.RawSha256.Split("; ");
        for (int i = 0; i < 2; i++)
            Assert.Equal(RunManifest.Sha256(File.ReadAllBytes(Path.Join(store.FolderOf(runId), files[i]))), hashes[i]);
        Assert.Equal("<feed>one</feed>", File.ReadAllText(Path.Join(store.FolderOf(runId), files[0])));
        Assert.All(xml.Asked, n => Assert.Equal(100, n)); // effort bounded: the plan's top 100
    }

    [Fact]
    public async Task DuplicatesAreFoundByDoiAndTitleAcrossDatabasesAndKeptOnFile()
    {
        var db = new FormalKit.Database();
        var (_, _, ledger) = await Search(k => k == "openalex" ? db : k == "arxiv" ? new Xml() : null);

        var formal = Assert.Single(ledger.FormalPool); // arXiv's paper has the same title as OpenAlex's first
        Assert.Equal("arxiv:1", formal.Paper!.Id);
        Assert.Equal(new[] { "arxiv: context AND agents", "arxiv: agent memory", "openalex: context AND agents", "openalex: agent memory" }, formal.FoundBy);
        Assert.Equal("Ana", formal.Record.Producer);
        Assert.Equal(MultivocalSearcher.FormalKind, formal.Record.Kind);
        Assert.Equal(new DateTime(2025, 1, 2), formal.Record.Published);
        var removed = ledger.FormalRemoved!;
        Assert.Equal(5, removed.Count(r => r.Reason == RemovedRecord.Duplicate));
        Assert.Equal(2, removed.Count(r => r.Reason == MultivocalSearcher.NoWebAddress));
        Assert.All(removed.Where(r => r.Reason == RemovedRecord.Duplicate), r => Assert.Equal("arxiv:1", r.DuplicateOf));
        Assert.Equal(2, ledger.GreyPool.Count());
    }

    [Fact]
    public async Task AFailingDatabaseIsRecordedWithoutItsKeyAndTheOthersStillRun()
    {
        var (_, _, ledger) = await Search(k => k == "arxiv" ? new Failing() : k == "openalex" ? new FormalKit.Database() : null);

        var failed = ledger.Searches.Where(s => s.Source == "arxiv").ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, s => Assert.Contains("HttpRequestException", s.Error));
        Assert.All(failed, s => Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123", s.Error));
        Assert.Single(ledger.FormalPool);
        Assert.Contains(ledger.Skipped, s => s.Source == "scopus" && s.Reason == "Needs a key this server does not have.");
    }

    [Fact]
    public async Task SaturationStopsADatabaseOnceAStringAddsNothingNew()
    {
        var xml = new Xml();
        var (_, _, ledger) = await Search(k => k == "arxiv" ? xml : null, p => { p.StoppingRule = "saturation"; p.FormalSearchStrings.Add("third"); });

        Assert.Equal(2, xml.Asked.Count); // the second string found only the same paper, so the third was not searched
        Assert.Contains("Theoretical saturation", ledger.StoppingApplied);
    }

    [Fact]
    public async Task AGreyLiteratureReviewsLedgerIsUnchangedAndNoDatabaseIsAsked()
    {
        bool asked = false;
        var (store, runId, ledger) = await Search(_ => { asked = true; return new Xml(); }, p => p.Card = "grey");

        Assert.False(asked);
        Assert.Null(ledger.FormalRemoved);
        string json = File.ReadAllText(Path.Join(store.FolderOf(runId), MultivocalSearcher.LedgerFile));
        var top = JsonDocument.Parse(json).RootElement;
        Assert.Equal(new[] { "RunId", "SearchedUtc", "StoppingApplied", "Searches", "Sources", "Skipped" }, top.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "Source", "SearchString", "Requested", "Returned", "New", "RawFile", "RawSha256", "SearchedUtc", "Error" },
            top.GetProperty("Searches")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "Record", "FoundBy" }, top.GetProperty("Sources")[0].EnumerateObject().Select(p => p.Name));
    }
}

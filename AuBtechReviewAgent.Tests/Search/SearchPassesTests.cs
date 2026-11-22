using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Pins down how the search stage runs the search strings against a source: the order and the budget per string,
/// the stop once the cap is met, the search log with each raw answer saved under its fingerprint, and a source
/// that fails.
/// </summary>
public class SearchPassesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "search-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class CountingSource : IAcademicSource
    {
        private int _next;
        public List<(string Query, int MaxResults)> Calls { get; } = new();
        public string SourceName => "Test Source!";
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
        {
            Calls.Add((query, maxResults));
            var papers = Enumerable.Range(0, 3).Select(_ =>
            {
                int n = Interlocked.Increment(ref _next);
                return new AcademicPaper($"T{n}", $"Agent paper number {n}", "An abstract about agents.", "Published: 2025", new() { "Jane Doe" }, "Future Internet");
            }).ToList();
            return Task.FromResult(papers);
        }
        public IReadOnlyList<string> LastRawResponses => new[] { "{\"page\":1}", "<feed>2</feed>" };
    }

    private sealed class FailingSource : IAcademicSource
    {
        public string SourceName => "Broken Source";
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5) => throw new HttpRequestException("connection refused");
    }

    private PrismaReviewEngine Engine(IAcademicSource source) => new("test-key", "", null, null, null, new RunsOptions())
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Evaluate the following academic paper") ? """{"decision":"Included","reasoning":"ok","briefSummary":"s"}""" : "{}"),
        SourceFactory = _ => source,
        FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
    };

    private static ReviewRequest Request(int cap) => new("agents", "o", "i", "e", cap, SelectedSources: new[] { "arxiv" }, DualScreening: false,
        SearchStrings: new[] { "agents", " Agent Safety ", "agent safety", "tools", "memory", "planning" });

    [Fact]
    public async Task TheStringsRunInOrderWithASharedBudgetAndStopOnceTheCapIsMet()
    {
        var source = new CountingSource();
        var engine = Engine(source);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request(10));
        var state = engine.LoadState(runId)!;

        Assert.Equal(new[] { "agents", "Agent Safety", "tools", "memory", "planning" }, state.SearchPerspectives);
        Assert.True(state.SearchStringsReviewed);
        // The cap of 10 is split over five strings (2 each); after four strings 12 distinct records are in, so the fifth is not run.
        Assert.Equal(new[] { ("agents", 2), ("Agent Safety", 2), ("tools", 2), ("memory", 2) }, source.Calls);

        Assert.Equal(new[] { "agents", "Agent Safety", "tools", "memory" }, state.SearchLogs.Select(l => l.QueryUsed));
        Assert.All(state.SearchLogs, l => Assert.Equal("Completed Successfully", l.Status));
        Assert.All(state.SearchLogs, l => Assert.Equal(3, l.PapersFound));
        Assert.Equal(12, state.Stats.TotalIdentified);
        Assert.Equal(2, state.Stats.CappedBeyondMaxResults);
        Assert.Equal(10, state.Phases.Screening.Count);
        Assert.All(state.Phases.Screening, l => Assert.Equal(PrismaReviewEngine.OriginDatabase, l.Origin));
    }

    [Fact]
    public async Task EveryRawAnswerIsSavedUnderItsFingerprint()
    {
        var engine = Engine(new CountingSource());
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request(10));
        var first = engine.LoadState(runId)!.SearchLogs[0];

        Assert.Equal("SourceResponses/01-test-source-q1-1.json; SourceResponses/01-test-source-q1-2.xml", first.RawResponseFile);
        string expected = RunManifest.Sha256(Encoding.UTF8.GetBytes("{\"page\":1}")) + "; " + RunManifest.Sha256(Encoding.UTF8.GetBytes("<feed>2</feed>"));
        Assert.Equal(expected, first.RawResponseSha256);

        string folder = Path.Join(_root, runId.ToString("N"), PrismaReviewEngine.RawResponsesFolder);
        Assert.Equal("<feed>2</feed>", File.ReadAllText(Path.Join(folder, "01-test-source-q1-2.xml")));
        Assert.Equal(8, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task ASourceThatFailsIsLoggedWithItsReasonAndNoRawFile()
    {
        var engine = Engine(new FailingSource());
        var runId = Guid.NewGuid();
        // Only the search is pinned here; what a run without records does afterwards is not.
        await Record.ExceptionAsync(() => engine.RunReviewAsync(runId, Request(4)));
        var state = engine.LoadState(runId)!;

        Assert.Equal(5, state.SearchLogs.Count);
        Assert.All(state.SearchLogs, l =>
        {
            Assert.Equal("Faulted / Refused Connection", l.Status);
            Assert.Contains("connection refused", l.ErrorMessage);
            Assert.Equal(0, l.PapersFound);
            Assert.Null(l.RawResponseFile);
        });
        Assert.Equal(0, state.Stats.TotalIdentified);
    }
}

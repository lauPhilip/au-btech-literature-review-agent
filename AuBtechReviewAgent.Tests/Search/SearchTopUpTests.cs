using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #681 (run 8942b961): with a cap of 10 and four strings each source asked for 3 records a string, and the records
/// dropped for their year were never replaced, so no source filled its cap. A round now asks for what the source still
/// needs, and a source still short after the last string gets one top-up pass.
/// </summary>
public class SearchTopUpTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "topup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    /// <summary>
    /// Answers every string with a long ranked list, the first <c>maxResults</c> of it, like a real database: every third
    /// record is from 2010, outside the years of the review. The ranked list is the same for the same string every time.
    /// </summary>
    private sealed class RankedSource(string name, int available = 40, Func<string, int, bool>? outside = null) : IAcademicSource
    {
        private readonly Func<string, int, bool> _outside = outside ?? ((_, i) => i % 3 == 0);
        public List<(string Query, int MaxResults)> Calls { get; } = new();
        public HashSet<string> Returned { get; } = new();
        public string SourceName => name;
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
        {
            lock (Calls) Calls.Add((query, maxResults));
            var papers = Enumerable.Range(0, Math.Min(available, maxResults)).Select(i =>
                new AcademicPaper($"{name}|{query}|{i}", $"{name} study of {query} number {i}", "An abstract about agent frameworks.",
                    $"Published: {(_outside(query, i) ? 2010 : 2024)}", new() { "Jane Doe" }, "Journal")).ToList();
            lock (Returned) foreach (var paper in papers) Returned.Add(paper.Id);
            return Task.FromResult(papers);
        }
    }

    private PrismaReviewEngine Engine(Func<string, IAcademicSource> sources) => new("test-key", "", null, null, null, new RunsOptions())
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Evaluate the following academic paper") ? """{"decision":"Included","reasoning":"ok","briefSummary":"s"}""" : "{}"),
        SourceFactory = sources,
        FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
    };

    private static ReviewRequest Request(int cap, params string[] strings) => new("agent orchestration", "o", "i", "e", cap, SelectedSources: new[] { "semanticscholar", "crossref" },
        DualScreening: false, YearFrom: 2020, YearTo: 2026, SearchStrings: strings);

    private static readonly string[] Four = { "agent orchestration", "plan reuse", "fault tolerance", "framework latency" };

    [Fact]
    public async Task EachSourceFillsItsCapWhenRecordsDropOutForTheirYearAndTheFlowAddsUp()
    {
        var s2 = new RankedSource("S2");
        var crossref = new RankedSource("CR");
        var engine = Engine(key => key == "crossref" ? crossref : s2);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request(10, Four));
        var state = engine.LoadState(runId)!;

        foreach (var prefix in new[] { "S2|", "CR|" })
            Assert.Equal(10, state.Phases.Screening.Count(l => l.PaperId.StartsWith(prefix, StringComparison.Ordinal)));
        var s = state.Stats;
        Assert.True(s.OutsideDateRange > 0);
        Assert.Equal(0, s.DuplicatesRemoved);
        Assert.Equal(s.TotalIdentified, s.DuplicatesRemoved + s.OutsideDateRange + s.CappedBeyondMaxResults + state.Phases.Screening.Count);
        Assert.Equal(s.TotalIdentified, s2.Returned.Count + crossref.Returned.Count);
        Assert.Equal(s.TotalIdentified, state.SearchLogs.Sum(l => l.PapersFound));
        Assert.Equal(s.OutsideDateRange, state.RemovedBeforeScreening.Count(r => r.Reason == RemovedRecord.OutsideYearRange));
        Assert.Equal(state.RemovedBeforeScreening.Count, state.RemovedBeforeScreening.Select(r => r.PaperId).Distinct().Count());
    }

    [Fact]
    public async Task ARoundAsksForWhatTheSourceStillNeedsAndTheTopUpAsksForMore()
    {
        var s2 = new RankedSource("S2", outside: (query, i) => query == "agent orchestration" ? i >= 1 : i % 3 == 0);
        var engine = Engine(_ => s2);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent orchestration", "o", "i", "e", 10, SelectedSources: new[] { "semanticscholar" },
            DualScreening: false, YearFrom: 2020, YearTo: 2026, SearchStrings: Four));
        var state = engine.LoadState(runId)!;

        // Round 1 asks 10 over 4 strings = 3 and keeps 1; round 2 asks the 9 still needed over the 3 strings left = 3.
        Assert.Equal(("agent orchestration", 3), s2.Calls[0]);
        Assert.Equal(("plan reuse", 3), s2.Calls[1]);
        Assert.True(s2.Calls.Count > 4, "a source still short of its cap is searched again");
        Assert.All(s2.Calls.Skip(4), call => Assert.True(call.MaxResults > s2.Calls.First(c => c.Query == call.Query).MaxResults));
        Assert.Equal(10, state.Phases.Screening.Count);
        // Each pass is in the search log, the top-up passes after the first four.
        Assert.Equal(s2.Calls.Count, state.SearchLogs.Count);
        Assert.Equal(state.Stats.TotalIdentified, state.SearchLogs.Sum(l => l.PapersFound));
    }

    [Fact]
    public async Task ASourceThatRunsOutOfRecordsStopsAndTheFlowStillAddsUp()
    {
        var s2 = new RankedSource("S2", available: 4);
        var engine = Engine(_ => s2);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent orchestration", "o", "i", "e", 10, SelectedSources: new[] { "semanticscholar" },
            DualScreening: false, YearFrom: 2020, YearTo: 2026, SearchStrings: Four));
        var state = engine.LoadState(runId)!;

        Assert.True(state.Phases.Screening.Count < 10);
        var s = state.Stats;
        Assert.Equal(s.TotalIdentified, s.DuplicatesRemoved + s.OutsideDateRange + s.CappedBeyondMaxResults + state.Phases.Screening.Count);
        Assert.Equal(s2.Returned.Count, s.TotalIdentified);
        // The top-up stops at one pass: no string is asked for a third time.
        Assert.All(s2.Calls.GroupBy(c => c.Query), g => Assert.True(g.Count() <= 2));
    }

    [Fact]
    public void TheRequestIsWhatIsStillNeededSharedOverTheStringsLeftAndAtLeastOne()
    {
        Assert.Equal(3, SearchLimits.Round(10, 0, 4));
        Assert.Equal(3, SearchLimits.Round(10, 2, 3));
        Assert.Equal(1, SearchLimits.Round(10, 9, 2));
        Assert.Equal(1, SearchLimits.Round(10, 10, 1));
        Assert.Equal(6, SearchLimits.TopUp(3, 10, 7));
    }

    [Fact]
    public void ASaturationCheckCountsATopUpOfAStringWithItsFirstSearch()
    {
        var first = new List<AcademicPaper> { new("a", "Paper a", "x", "2024", new(), "J") };
        var topUp = new List<AcademicPaper> { new("b", "Paper b", "x", "2024", new(), "J") };
        var yields = SearchSaturation.Compute(new[] { "one", "two" }, new (int, IReadOnlyList<AcademicPaper>)[] { (0, first.Concat(topUp).ToList()), (1, first) });

        Assert.Equal(2, yields[0].New);
        Assert.Equal(0, yields[1].New);
    }
}

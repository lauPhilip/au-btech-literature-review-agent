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

    /// <summary>A source that answers each search string with the records listed for it.</summary>
    private sealed class ScriptedSource(string name, Dictionary<string, AcademicPaper[]> answers) : IAcademicSource
    {
        public List<string> Calls { get; } = new();
        public string SourceName => name;
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
        {
            lock (Calls) Calls.Add(query);
            return Task.FromResult(answers.TryGetValue(query, out var papers) ? papers.ToList() : new List<AcademicPaper>());
        }
    }

    private static AcademicPaper Paper(string id, string title, int year, string? doi = null) =>
        new(id, title, "An abstract about agent frameworks.", $"Published: {year}", new() { "Jane Doe" }, "Journal") { Doi = doi };

    private PrismaReviewEngine Engine(IAcademicSource source) => Engine(_ => source);

    private PrismaReviewEngine Engine(Func<string, IAcademicSource> sources) => new("test-key", "", null, null, null, new RunsOptions())
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Evaluate the following academic paper") ? """{"decision":"Included","reasoning":"ok","briefSummary":"s"}""" : "{}"),
        SourceFactory = sources,
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
        // A round asks for what the source still needs shared over the strings left (10 over 5, 7 over 4, 4 over 3, 1 over 2);
        // after four strings 12 distinct records are in, so the fifth is not run.
        Assert.Equal(new[] { ("agents", 2), ("Agent Safety", 2), ("tools", 2), ("memory", 1) }, source.Calls);

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

    [Fact]
    public async Task ADuplicateAndARecordOutsideTheYearsDoNotUseUpTheCapOfRun289a0d3e()
    {
        // Run 289a0d3e: Crossref's two records for the first two strings were a duplicate of a Semantic Scholar record
        // and a record from 2006, so Crossref sent nothing to screening and the last two strings were never run (#562).
        var shared = Paper("S2_1817", "Agentic AI Frameworks for Autonomous Enterprise Software Development Workflows", 2025, "10.63282/3050-9416.ijaibdcms-v6i1p122");
        var s2 = new ScriptedSource("Semantic Scholar", new()
        {
            ["agentic AI software frameworks"] = new[] { shared },
            ["autonomous multi-agent system frameworks"] = new[] { Paper("S2_ba27", "Large Language Model Based Multi-Agent System Augmented Complex Event Processing Pipeline", 2024) },
        });
        var crossref = new ScriptedSource("Crossref", new()
        {
            ["agentic AI software frameworks"] = new[] { shared with { Id = "CROSSREF_10.63282/3050-9416.ijaibdcms-v6i1p122" } },
            ["autonomous multi-agent system frameworks"] = new[] { Paper("CROSSREF_10.1007/s10458-006-0009-8", "Adaptive decision-making frameworks for dynamic multi-agent organizational change", 2006, "10.1007/s10458-006-0009-8") },
            ["agent orchestration error recovery"] = new[] { Paper("CROSSREF_a", "Error recovery in agent orchestration", 2024) },
            ["evaluating framework fault tolerance"] = new[] { Paper("CROSSREF_b", "Evaluating the fault tolerance of agent frameworks", 2025) },
        });
        var engine = Engine(key => key == "crossref" ? crossref : s2);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agentic AI software frameworks", "o", "i", "e", 2,
            SelectedSources: new[] { "semanticscholar", "crossref" }, DualScreening: false, YearFrom: 2020, YearTo: 2026,
            SearchStrings: new[] { "autonomous multi-agent system frameworks", "agent orchestration error recovery", "evaluating framework fault tolerance" }));
        var state = engine.LoadState(runId)!;

        // Semantic Scholar filled its cap of 2 after two strings and stopped; Crossref went on until it had 2 kept records.
        Assert.Equal(new[] { "agentic AI software frameworks", "autonomous multi-agent system frameworks" }, s2.Calls);
        Assert.Equal(4, crossref.Calls.Count);
        Assert.Equal(1, state.Stats.DuplicatesRemoved);
        Assert.Equal(1, state.Stats.OutsideDateRange);
        Assert.Equal(0, state.Stats.CappedBeyondMaxResults);
        Assert.Equal(new[] { "S2_1817", "S2_ba27", "CROSSREF_a", "CROSSREF_b" }, state.Phases.Screening.Select(l => l.PaperId));
        Assert.Equal(new[] { 2, 2, 1, 1 }, state.SearchStringYields.Select(y => y.Searches));
    }
}

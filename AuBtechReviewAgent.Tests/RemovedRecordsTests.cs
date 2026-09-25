using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Records removed before screening must be listed in the ledger with their reason, and duplicates must be
/// caught across sources (the same paper from arXiv and Google Scholar counts once).
/// </summary>
public class RemovedRecordsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "removed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private class ListSource : IAcademicSource
    {
        private readonly List<AcademicPaper> _papers;
        public ListSource(string name, List<AcademicPaper> papers) { SourceName = name; _papers = papers; }
        public string SourceName { get; }
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5) => Task.FromResult(_papers.ToList());
    }

    private static AcademicPaper Paper(string id, string title, int year, string? doi = null) =>
        new(id, title, "An abstract about agents.", $"Published: {year}", new() { "Jane Doe" }, "Future Internet", doi);

    [Fact]
    public async Task RemovedRecordsAreListedWithTheirReasonAndMatchTheCounters()
    {
        var arxiv = new ListSource("arXiv API", new()
        {
            Paper("http://arxiv.org/abs/1", "Agent loops for safety", 2025, "10.1000/abc"),
            Paper("http://arxiv.org/abs/2", "Agent governance", 2024),
            Paper("http://arxiv.org/abs/3", "An old agent paper", 2012),
        });
        var scholar = new ListSource("Google Scholar Gateway", new()
        {
            Paper("SCHOLAR_x", "Agent Loops for Safety.", 2025),               // same title, other source
            Paper("SCHOLAR_y", "Completely different title", 2025, "https://doi.org/10.1000/ABC"), // same DOI
            Paper("SCHOLAR_z", "Agent memory", 2025),
            Paper("SCHOLAR_w", "Agent planning", 2025),
            Paper("SCHOLAR_v", "Agent tools", 2025),                         // third candidate: over the cap of 2
        });

        // A SerpApi key is needed or the Scholar source is treated as unavailable and never queried.
        var engine = new PrismaReviewEngine("test-key", "", null, "test-serpapi-key", null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(prompt =>
                prompt.Contains("Propose exactly 3 additional") ? """{"perspectives":[]}"""
                : prompt.Contains("Evaluate the following academic paper") ? """{"decision":"Included","reasoning":"ok","briefSummary":"s"}"""
                : "{}"),
            SourceFactory = key => key == "arxiv" ? arxiv : scholar,
        };
        var runId = Guid.NewGuid();

        await engine.RunReviewAsync(runId, new ReviewRequest("agents", "o", "i", "e", 2,
            SelectedSources: new[] { "arxiv", "scholar" }, YearFrom: 2020, YearTo: 2026));

        var state = engine.LoadState(runId)!;
        var removed = state.RemovedBeforeScreening;

        Assert.Equal(2, state.Stats.DuplicatesRemoved);
        Assert.Equal(1, state.Stats.OutsideDateRange);
        Assert.Equal(1, state.Stats.CappedBeyondMaxResults);
        Assert.Equal(state.Stats.DuplicatesRemoved + state.Stats.OutsideDateRange + state.Stats.CappedBeyondMaxResults, removed.Count);
        Assert.True(PrismaFlowCounts.From(state).IsConsistent);

        var byTitle = removed.Single(r => r.PaperId == "SCHOLAR_x");
        Assert.Equal(RemovedRecord.Duplicate, byTitle.Reason);
        Assert.Equal("http://arxiv.org/abs/1", byTitle.DuplicateOf);

        var byDoi = removed.Single(r => r.PaperId == "SCHOLAR_y");
        Assert.Equal("http://arxiv.org/abs/1", byDoi.DuplicateOf);
        Assert.Equal("10.1000/abc", byDoi.Doi);

        var old = removed.Single(r => r.Reason == RemovedRecord.OutsideYearRange);
        Assert.Equal(2012, old.Year);
        Assert.Equal("arXiv API", old.Source);

        Assert.Equal("SCHOLAR_v", removed.Single(r => r.Reason == RemovedRecord.OverCap).PaperId);
    }

    [Theory]
    [InlineData("https://doi.org/10.1000/ABC", "10.1000/abc")]
    [InlineData("doi:10.48550/arXiv.2607.02703", "10.48550/arxiv.2607.02703")]
    [InlineData("XXX", null)]
    [InlineData(null, null)]
    public void DoisAreNormalisedForDuplicateDetection(string? input, string? expected)
    {
        Assert.Equal(expected, PrismaReviewEngine.NormalizeDoi(input));
    }

    [Fact]
    public void TitlesIgnoreCaseAndPunctuation()
    {
        Assert.Equal(PrismaReviewEngine.NormalizeTitle("Agent Loops for Safety."), PrismaReviewEngine.NormalizeTitle("agent loops  for safety"));
    }
}

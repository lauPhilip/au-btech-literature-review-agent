using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class GroundingAndFlowTests
{
    [Fact]
    public void EveryIncludedPaperIsRepresentedEvenWhenOnePdfIsLong()
    {
        var longPaper = new ReferencedPaper(1, "long", "Long paper", "Long abstract.",
            Enumerable.Range(1, 80).Select(i => new DocumentChunk("long", i, $"chunk {i} about agent loops")).ToList());
        var shortPaper = new ReferencedPaper(2, "short", "Short paper", "Short abstract about safety.", Array.Empty<DocumentChunk>());

        string context = GroundingContextBuilder.Build(new[] { longPaper, shortPaper }, "agent loops safety", totalChunkBudget: 40);

        Assert.Contains("[Reference 2] Short paper", context);
        Assert.Contains("Short abstract about safety.", context);
        Assert.Contains("Full text was not available", context);
        Assert.Equal(20, context.Split('\n').Count(l => l.StartsWith("[Reference 1, page")));
    }

    [Fact]
    public void MostRelevantChunksAreChosenAndKeptInPageOrder()
    {
        var chunks = new List<DocumentChunk>
        {
            new("p", 1, "introduction and background"),
            new("p", 2, "fault tolerance in agent orchestration loops"),
            new("p", 3, "acknowledgements"),
            new("p", 4, "orchestration loops recover from agent faults"),
        };

        var chosen = GroundingContextBuilder.SelectChunks(chunks, TextRelevance.Terms("agent orchestration loops fault"), 2);

        Assert.Equal(new[] { 2, 4 }, chosen.Select(c => c.PageNumber));
    }

    private static ReviewState ConsistentState()
    {
        var state = new ReviewState();
        state.SearchLogs.Add(new PlatformSearchLog { SourceName = "arXiv API", PapersFound = 7 });
        state.SearchLogs.Add(new PlatformSearchLog { SourceName = "IEEE Xplore API", PapersFound = 3 });
        var s = state.Stats;
        s.TotalIdentified = 10; s.DuplicatesRemoved = 2; s.OutsideDateRange = 1; s.CappedBeyondMaxResults = 1;
        s.Screened = 6; s.FailedPeerReviewCheck = 1; s.Excluded = 2; s.Included = 3; s.FullTextRetrieved = 2;
        return state;
    }

    [Fact]
    public void FlowCountsAddUp()
    {
        var flow = PrismaFlowCounts.From(ConsistentState());

        Assert.True(flow.IsConsistent);
        Assert.Equal(1, flow.AbstractOnly);
        Assert.Equal(2, flow.IdentifiedBySource.Count);
    }

    [Fact]
    public void FlowCountsDetectAMismatch()
    {
        var state = ConsistentState();
        state.Stats.TotalIdentified = 12;

        Assert.False(PrismaFlowCounts.From(state).IsConsistent);
    }

    [Fact]
    public void FlowDiagramShowsTheNumbers()
    {
        string tikz = PrismaFlowDiagram.ToTikz(PrismaFlowCounts.From(ConsistentState()));

        Assert.Contains("Records identified from databases (n = 10)", tikz);
        Assert.Contains("arXiv API (n = 7)", tikz);
        Assert.Contains("Duplicates (n = 2)", tikz);
        Assert.Contains("Not peer reviewed (n = 1)", tikz);
        Assert.Contains("Studies included in review (n = 3)", tikz);
        Assert.Contains("abstract only (n = 1)", tikz);
    }

    [Fact]
    public void ExpiredRunFoldersAreDeletedButRecentAndActiveOnesAreKept()
    {
        string store = Path.Combine(Path.GetTempPath(), "store-" + Guid.NewGuid().ToString("N"));
        Guid oldRun = Guid.NewGuid(), activeOldRun = Guid.NewGuid(), recentRun = Guid.NewGuid();
        try
        {
            foreach (var id in new[] { oldRun, activeOldRun, recentRun })
            {
                string dir = Path.Combine(store, id.ToString("N"));
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "transparent-process.json");
                File.WriteAllText(file, "{}");
                DateTime stamp = id == recentRun ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(-10);
                File.SetLastWriteTimeUtc(file, stamp);
                Directory.SetLastWriteTimeUtc(dir, stamp);
            }

            int deleted = SessionCleanupWorker.PurgeExpiredRuns(store, TimeSpan.FromDays(7), DateTime.UtcNow, id => id == activeOldRun);

            Assert.Equal(1, deleted);
            Assert.False(Directory.Exists(Path.Combine(store, oldRun.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(store, activeOldRun.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(store, recentRun.ToString("N"))));
        }
        finally
        {
            try { Directory.Delete(store, true); } catch { }
        }
    }

    [Fact]
    public void AFreshlyRewrittenLedgerKeepsAnOldFolderAlive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "transparent-process.json");
            File.WriteAllText(file, "{}");
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-30));

            Assert.True(SessionCleanupWorker.LastActivityUtc(dir) > DateTime.UtcNow.AddMinutes(-5));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

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
    public void FlowDiagramEscapesSourceNamesAndReasonsWithTheSharedEscape()
    {
        // Issue #538: the hand-written escape left a backslash, braces, "^" and "~" as LaTeX.
        var flow = new PrismaFlowCounts
        {
            IdentifiedBySource = { (@"Evil\input{/etc/passwd}", 2) },
            ExcludedAtScreening = 1, ExcludedByReason = { ("Off topic ^^5c & 50% ~", 1) },
        };
        string tikz = PrismaFlowDiagram.ToTikz(flow);

        Assert.Contains(PaperLatex.Escape(@"Evil\input{/etc/passwd}") + " (n = 2)", tikz);
        Assert.Contains(@"\quad " + PaperLatex.Escape("Off topic ^^5c & 50% ~") + " (n = 1)", tikz);
        Assert.DoesNotContain(@"\input{", tikz);
        Assert.DoesNotContain("^^5c", tikz);
    }

    [Fact]
    public void FlowDiagramHasTheRetrievalAndEligibilityBoxesAndSaysNoneIsExcludedOnFullText()
    {
        string tikz = PrismaFlowDiagram.ToTikz(PrismaFlowCounts.From(ConsistentState()));

        // PRISMA 2020 item 16a: every included study's report is sought, one had no readable full text and was kept.
        Assert.Contains("Reports sought for retrieval (n = 3)", tikz);
        Assert.Contains("Reports not retrieved (n = 1)", tikz);
        Assert.Contains("Kept and read from the abstract only", tikz);
        Assert.Contains("Reports assessed for eligibility (n = 3)", tikz);
        Assert.Contains("on title and abstract at screening", tikz);
        Assert.Contains("Reports excluded (n = 0)", tikz);
        Assert.Contains("Reports of included studies (n = 3)", tikz);
        // The boxes run in PRISMA's order, one arrow into each.
        Assert.True(tikz.IndexOf("(screened) -- (sought)", StringComparison.Ordinal) > 0);
        Assert.True(tikz.IndexOf("(sought) -- (assessed)", StringComparison.Ordinal) > 0);
        Assert.True(tikz.IndexOf("(assessed) -- (included)", StringComparison.Ordinal) > 0);
    }

    [Fact]
    public void ReportsNotRetrievedGiveTheirReasonsOnlyWhenTheReasonsAddUp()
    {
        var state = ConsistentState();
        state.FullTextSources["p1"] = "none (download failed: 403 Forbidden)";
        state.FullTextSources["p2"] = "unpaywall";
        var flow = PrismaFlowCounts.From(state);

        Assert.Equal(new[] { ("Download failed", 1) }, flow.NotRetrievedByReason);
        Assert.Contains(@"Reports not retrieved (n = 1):\\ Download failed (n = 1)\\ Kept", PrismaFlowDiagram.ToTikz(flow));

        // A run that recorded a reason for more studies than were read from the abstract shows the count alone.
        state.FullTextSources["p3"] = "none (no open-access copy known)";
        Assert.Empty(PrismaFlowCounts.From(state).NotRetrievedLines());
        Assert.Equal(new[] { ("Download failed", 1), ("No open-access copy known", 1) }, PrismaFlowCounts.NotRetrievedReasons(state.FullTextSources.Values));
    }

    [Fact]
    public void ExpiredRunFoldersAreDeletedButRecentAndActiveOnesAreKept()
    {
        string store = Path.Join(Path.GetTempPath(), "store-" + Guid.NewGuid().ToString("N"));
        Guid oldRun = Guid.NewGuid(), activeOldRun = Guid.NewGuid(), recentRun = Guid.NewGuid();
        try
        {
            foreach (var id in new[] { oldRun, activeOldRun, recentRun })
            {
                string dir = Path.Join(store, id.ToString("N"));
                Directory.CreateDirectory(dir);
                string file = Path.Join(dir, "transparent-process.json");
                File.WriteAllText(file, "{}");
                DateTime stamp = id == recentRun ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(-10);
                File.SetLastWriteTimeUtc(file, stamp);
                Directory.SetLastWriteTimeUtc(dir, stamp);
            }

            int deleted = SessionCleanupWorker.PurgeExpiredRuns(store, TimeSpan.FromDays(7), DateTime.UtcNow, id => id == activeOldRun);

            Assert.Equal(1, deleted);
            Assert.False(Directory.Exists(Path.Join(store, oldRun.ToString("N"))));
            Assert.True(Directory.Exists(Path.Join(store, activeOldRun.ToString("N"))));
            Assert.True(Directory.Exists(Path.Join(store, recentRun.ToString("N"))));
        }
        finally
        {
            TestFolders.TryDelete(store);
        }
    }

    [Fact]
    public void AFreshlyRewrittenLedgerKeepsAnOldFolderAlive()
    {
        string dir = Path.Join(Path.GetTempPath(), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Join(dir, "transparent-process.json");
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

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #298: the grey literature review counts the citations the second repair pass removed, as the systematic
/// review does since #295, in its metrics row, its paper's verdict bar and its LaTeX note on the citation check.
/// </summary>
public class MultivocalRemovedCitationsTests
{
    private static MultivocalCitationAudit Audit(int removed, params int[] passes) => new()
    {
        InitialSupportSummary = new CitationSupportSummary { Checked = 10, NotSupported = 3, PartiallySupported = 2 },
        SupportSummary = new CitationSupportSummary { Checked = 10 - removed, Supported = 7, PartiallySupported = 1, Removed = removed },
        Repairs = passes.Select((p, i) => new CitationRepair("synthesis-1", $"Before {i}.", $"After {i}.", "rewrite", new[] { 1 }, "[1] not_supported", "", p)).ToList(),
        RemovedCitations = Enumerable.Range(1, removed)
            .Select(i => new RemovedCitation("discussion", $"Claim {i} [{i}].", i, CitationSupportChecker.NotSupported, null, "Still not supported.", true)).ToList(),
    };

    [Fact]
    public void TheMetricsRowCountsTheRemovedCitations()
    {
        var input = new MultivocalReportInput(new PlannedRun(Guid.NewGuid(), new MultivocalPlan(), "", DateTime.UtcNow), new MultivocalLedger(), new MultivocalScreeningFile(), new MultivocalPagesFile(),
            new MultivocalQualityFile(), new MultivocalMapFile(), new MultivocalExtractionFile(), new MultivocalSynthesisFile());

        var metrics = MultivocalRunLog.BuildMetrics(input, new MultivocalPaperFile(), Audit(2, 1, 1, 2), Array.Empty<LlmCallRecord>());

        Assert.Equal(2, metrics.Removed);
        Assert.Equal(3, metrics.Repaired);
    }

    [Fact]
    public void TheLatexNoteSaysEachPassAndWhatWasRemoved()
    {
        string note = MultivocalPaper.CheckNote(Audit(2, 1, 1, 2));

        Assert.Contains("2 cited sentences were rewritten or lost a citation and were checked again, and 1 that were still partly supported were rewritten a second time and checked again. ", note);
        Assert.Contains("2 citations that were still not supported after the repair were removed, with the claim when no other citation backed it; they are not in the counts above", note);
        Assert.DoesNotContain(" once ", note);
    }

    [Fact]
    public void WithoutASecondPassOrRemovalsTheNoteIsAsBefore()
    {
        string note = MultivocalPaper.CheckNote(Audit(0, 1, 1));

        Assert.Contains("2 cited sentences were rewritten or lost a citation and were checked again. ", note);
        Assert.DoesNotContain("second time", note);
        Assert.DoesNotContain("removed, with the claim", note);
    }

    [Fact]
    public void ThePaperPageBarCountsTheRemovedCitations()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository (packaged tests)
        string page = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Modules", "Multivocal", "Pages", "MultivocalPaperPage.razor"));
        Assert.Contains("VerdictTally.From(checks, audit?.RemovedCitations.Count ?? 0)", page);
    }
}

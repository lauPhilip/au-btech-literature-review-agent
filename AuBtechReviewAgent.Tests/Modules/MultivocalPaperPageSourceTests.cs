using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The grey literature review's paper page (Modules/Multivocal/Pages/MultivocalPaperPage.razor and its code-behind),
/// read as source: what it loads, how it steps through the citations and what it asks of the browser.
/// </summary>
public class MultivocalPaperPageSourceTests
{
    private static string? PageSource()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return null; // not run from the repository (packaged tests)
        string folder = Path.Join(root, "AuBtechReviewAgent", "Modules", "Multivocal", "Pages");
        return string.Join("\n", Directory.GetFiles(folder, "MultivocalPaperPage.razor*").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
    }

    [Fact]
    public void ThePageLoadsTheRunsFilesAndSendsOtherRunsToTheirOwnPage()
    {
        string? page = PageSource();
        if (page == null) return;
        Assert.Contains("@page \"/mlr-paper/{RunId:guid}\"", page);
        Assert.Contains("Nav.NotFound()", page);
        Assert.Contains("ReviewModules.ElsewhereFor(Runs.LoadHeader(RunId), module.Key)", page);
        Assert.Contains("input = Reporter.LoadInput(RunId);", page);
        Assert.Contains("paper = Writer.Load(RunId);", page);
        Assert.Contains("audit = Writer.LoadAudit(RunId);", page);
        Assert.Contains("GreyReferences.Build(input.Ledger, input.Pages, input.Extraction)", page);
    }

    [Fact]
    public void ThePageStepsThroughTheCitationsInReadingOrder()
    {
        string? page = PageSource();
        if (page == null) return;
        Assert.Contains("if (field == MultivocalWriter.RationaleField) return -1;", page.Replace("AuBtechReviewAgent.", ""));
        Assert.Contains("return field == MultivocalWriter.DiscussionField ? 2000 : 3000;", page.Replace("AuBtechReviewAgent.", ""));
        Assert.Contains("CitationView.Attention(checks, FieldRank)", page);
        Assert.Contains("ReportOutline.InOrder(checks, checkFilter, FieldRank)", page);
        Assert.Contains("ReportOutline.Multivocal(paper!)", page);
        Assert.Contains("$\"{flow.Extracted} sources · {flow.Found} found · {flow.Themes} themes · {checks.Count} citations checked\"", page);
    }

    [Fact]
    public void ThePageCallsTheBrowserOnlyThroughTheSiteScript()
    {
        string? page = PageSource();
        if (page == null) return;
        foreach (string call in new[] { "traceableReview.focus", "traceableReview.scrollTo", "traceableReview.print" })
            Assert.Contains($"\"{call}\"", page);
        Assert.Contains("pendingFocus = closing ? null : $\"verdict-{field}\";", page);
    }
}

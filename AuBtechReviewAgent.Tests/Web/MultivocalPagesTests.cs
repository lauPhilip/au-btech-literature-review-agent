using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Redesign step 11: the grey literature review's pages mirror the systematic review's. The paper's outline
/// (ReportOutline.Multivocal) and the pages themselves (Modules/Multivocal/Pages, read as source).
/// </summary>
public class MultivocalPagesTests
{
    private static string? Source(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent" }.Concat(path).ToArray()));
    }

    private static MultivocalPaperFile Paper(bool summary) => new()
    {
        Title = "Grey literature on agent safety",
        Results = new List<PaperResult>
        {
            new() { Kind = "theme", Question = "RQ1", Heading = "Guardrails", Field = "synthesis-1" },
            new() { Kind = "code", Question = "RQ2", Heading = "Kinds of sources", Field = "answer-RQ2" },
        },
        SummaryItems = summary ? new List<string> { "Use guardrails." } : new List<string>(),
    };

    [Fact]
    public void TheOutlineListsEveryResultUnderSectionFour()
    {
        var outline = ReportOutline.Multivocal(Paper(summary: true));

        Assert.Equal(new[] { "abstract", "s1", "s2", "s3", "s4", "s4-1", "s4-2", "s5", "s6", "declarations", "references" }, outline.Select(e => e.Id));
        Assert.Equal("4.1 RQ1: Guardrails", outline.Single(e => e.Id == "s4-1").Label);
        Assert.Equal("4.2 Kinds of sources", outline.Single(e => e.Id == "s4-2").Label);
        Assert.Equal(2, outline.Single(e => e.Id == "s4-1").Level);
        Assert.Equal("7 Declarations", outline.Single(e => e.Id == "declarations").Label);
    }

    [Fact]
    public void WithoutASummaryTheDeclarationsAreSectionSix()
    {
        var paper = Paper(summary: false);
        var outline = ReportOutline.Multivocal(paper);

        Assert.DoesNotContain(outline, e => e.Id == ReportOutline.SummaryId);
        Assert.Equal(6, ReportOutline.DeclarationsNumber(paper));
        Assert.Equal("6 Declarations", outline.Single(e => e.Id == "declarations").Label);
    }

    [Fact]
    public void ThePaperPageGivesEverySectionItsOutlineId()
    {
        string? page = Source("Modules", "Multivocal", "Pages", "MultivocalPaperPage.razor");
        if (page == null) return; // not run from the repository (packaged tests)

        foreach (string id in new[] { "AbstractId", "IntroductionId", "MethodologyId", "StudiesId", "ResultsId", "DiscussionId", "SummaryId", "DeclarationsId", "ReferencesId" })
            Assert.Contains($"id=\"@AuBtechReviewAgent.ReportOutline.{id}\" data-report-section", page);
        Assert.Contains("id=\"@AuBtechReviewAgent.ReportOutline.ResultId(k)\" data-report-section", page);
        Assert.Contains("<ReportContents Entries=\"Outline\"", page);
        Assert.Contains("<CitationCheckPanel ", page);
        Assert.Contains("Multivocal=\"true\"", page);
        Assert.Contains("HideFromLarge=\"true\"", page);
        // Everything the old page offered is still there.
        foreach (string kept in new[] { "/archive", "/main.tex", "/references.bib", "/references.ris", "/screened.ris", "/mlr-report/", "PrintPaper" })
            Assert.Contains(kept, page);
        Assert.DoesNotContain("max-w-[8.5in]", page);
    }

    [Fact]
    public void TheMultivocalPagesUseTheThemeColours()
    {
        foreach (string file in new[] { "MultivocalPaperPage.razor", "MultivocalReportPage.razor", "MultivocalStart.razor", "MultivocalProgressPanel.razor" })
        {
            string? page = Source("Modules", "Multivocal", "Pages", file);
            if (page == null) return;
            Assert.DoesNotContain("text-gray-", page);
            Assert.DoesNotContain("border-gray-", page);
            Assert.DoesNotContain("ui-h2", page);
        }
    }
}

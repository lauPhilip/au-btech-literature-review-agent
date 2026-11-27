using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// What sits in the paper's body on the Review output page and what sits beside or under it (Lau, 27 November 2026).
/// Issue #735: the self-check is about the tool, not part of the paper, so its table is under the report, not after the
/// front page; Article info keeps its one line. Issue #738: the diagram's cited boxes are listed in the appendix, with a
/// pointer under the figure, so the results do not repeat the diagram.
/// </summary>
public class ReportBodyTests
{
    [Fact]
    public void TheSelfCheckIsUnderTheReportNotInItsBody()
    {
        string? page = ReportPageSource.Read();
        string? header = PageSource.Read("SpecMatrixHeader");
        if (page == null || header == null) return; // not run from the repository (packaged tests)

        Assert.DoesNotContain("<SelfCheckTable", header);
        int table = page.IndexOf("<SelfCheckTable Result=\"selfCheck\"", StringComparison.Ordinal);
        Assert.True(table > page.LastIndexOf("</article>", StringComparison.Ordinal), "the self-check table comes after the paper");
    }
    [Fact]
    public void TheCitedBoxesAreInTheAppendixWithAPointerUnderTheFigure()
    {
        string? results = PageSource.Read("SpecMatrixResults");
        string? appendix = PageSource.Read("SpecMatrixAppendix");
        if (results == null || appendix == null) return;

        Assert.DoesNotContain("Cited boxes in the diagram", results);
        Assert.Contains("data-cited-boxes-pointer", results);
        Assert.Contains("The citations of each box are checked in", results);
        Assert.Contains("id=\"@AuBtechReviewAgent.ReportOutline.CitedBoxesId\"", appendix);
        Assert.Contains("CitedText($\"artifact-n{box + 1}\"", appendix); // PageSource takes "Page." out
    }

    [Fact]
    public void OnlyTheBoxesOfADiagramThatCiteAStudyAreListed()
    {
        var diagram = new ReviewArtifact
        {
            Kind = ArtifactKinds.ConceptMap,
            Nodes = { new ArtifactNode { Label = "Agents" }, new ArtifactNode { Label = "Supervisors recover faults [1]" }, new ArtifactNode { Label = "Audits [2, 3]" } },
        };

        Assert.Equal(new[] { 1, 2 }, ReportOutline.CitedBoxes(diagram));
        Assert.Empty(ReportOutline.CitedBoxes(new ReviewArtifact { Kind = ArtifactKinds.Table, Nodes = diagram.Nodes }));
        Assert.Empty(ReportOutline.CitedBoxes(new ReviewArtifact { Kind = ArtifactKinds.ConceptMap, Nodes = diagram.Nodes, Error = "failed" }));
        Assert.Empty(ReportOutline.CitedBoxes(null));
        Assert.Equal("Table A.4", ReportOutline.CitedBoxesTable(hasExtractions: true));
        Assert.Equal("Table A.2", ReportOutline.CitedBoxesTable(hasExtractions: false));
    }
}

using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Three faults of the report preview (issues #637, #628 and #635), read as source: an opened citation closes with a click
/// outside it or Escape and never with the click that opens it; a table that runs over a page repeats its header row; and
/// the box of citation checks is not in the pages, because the panel beside them gives the same figures.
/// </summary>
public class ReportPreviewTests
{
    private static string Read(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? "" : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent" }.Concat(path).ToArray()));
    }

    [Fact]
    public void TheCitationCheckBoxIsNotInThePagesAndThePanelBesideThemStays()
    {
        string? page = ReportPageSource.Read();
        if (page == null) return;
        Assert.DoesNotContain("checked against the papers", page);
        Assert.DoesNotContain("<VerdictBar", page);
        Assert.Contains("<CitationCheckPanel ", page);
        Assert.Contains("<CitationAttentionBar ", page);
    }
    [Fact]
    public void TheRouterSettingParametersAgainDoesNotCloseTheOpenCitation()
    {
        string code = Read("Components", "Pages", "SpecMatrix.razor.cs");
        if (code.Length == 0) return;
        Assert.Contains("if (openedFor != State.CurrentSessionId || InGallery) openCitation = null;", code);
    }
}

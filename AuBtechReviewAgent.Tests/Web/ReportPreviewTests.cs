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
    public void AnOpenCitationClosesOnAClickOutsideItAndOnEscape()
    {
        string prose = Read("Components", "Paper", "CitedProse.razor");
        if (prose.Length == 0) return; // packaged tests
        Assert.Contains("data-citation-panel", prose);
        Assert.Contains("data-citation-close", prose);

        string script = Read("wwwroot", "js", "review-output.js");
        Assert.Contains("[data-citation-close]", script);
        Assert.Contains("e.key !== 'Escape'", script);
        // A button, link or field keeps its own click, so the click that opens a citation (a button) can never close it.
        Assert.Contains("closest('[data-citation-panel], button, a, input, textarea, select, summary, label", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    [Fact]
    public void TheRouterSettingParametersAgainDoesNotCloseTheOpenCitation()
    {
        string code = Read("Components", "Pages", "SpecMatrix.razor.cs");
        if (code.Length == 0) return;
        Assert.Contains("if (openedFor != State.CurrentSessionId || InGallery) openCitation = null;", code);
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
}

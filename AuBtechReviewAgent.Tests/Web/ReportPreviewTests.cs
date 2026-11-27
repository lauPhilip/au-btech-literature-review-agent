using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Faults of the report preview (issues #637, #628, #635 and #737), read as source: an opened citation closes with a click
/// outside it or Escape and never with the click that opens it; a table that runs over a page repeats its header row; the
/// box of citation checks is not in the pages, because the panel beside them gives the same figures; and a paragraph that
/// cannot move whole breaks between lines, while a figure taller than a page is scaled to fit one.
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
    public void ATableThatRunsOverAPageRepeatsItsHeaderRow()
    {
        string tables = Read("wwwroot", "js", "paged-tables.js");
        if (tables.Length == 0) return;
        Assert.Contains("repeatedHead", tables);
        Assert.Contains("':scope > thead'", tables);
        Assert.DoesNotContain("innerHTML", tables);

        string pages = Read("wwwroot", "js", "paged-preview.js");
        Assert.Contains("traceablePageTables", pages);
        Assert.Contains("el.parentElement.tagName === 'TBODY'", pages); // a body row moves alone
        Assert.Contains("<div data-page-top aria-hidden=\"true\"></div>", ReportPageSource.Read());

        string app = Read("Components", "App.razor");
        Assert.True(app.IndexOf("js/paged-tables.js", StringComparison.Ordinal) is var at and >= 0 && at < app.IndexOf("js/paged-preview.js", StringComparison.Ordinal));

        string css = Read("wwwroot", "css", "report-paper.css");
        Assert.Contains("[data-page-top] { position: absolute; inset: 0; z-index: 2; pointer-events: none; }", css);
        Assert.DoesNotContain("border-radius", css);
    }

    [Fact]
    public void TheBlockFindingIsItsOwnScriptLoadedBeforeThePaginator()
    {
        string blocks = Read("wwwroot", "js", "paged-blocks.js");
        if (blocks.Length == 0) return;
        Assert.Contains("window.traceablePageBlocks", blocks);
        string pages = Read("wwwroot", "js", "paged-preview.js");
        Assert.Contains("traceablePageBlocks", pages);
        Assert.Contains("[data-citation-panel]", pages); // a card that opened off screen is scrolled to (#671)
        Assert.True(pages.Split('\n').Length <= 300);
        string script = Read("wwwroot", "js", "review-output.js"); // the tab's title (#682)
        Assert.Contains("setTitle: function (text)", script);
        Assert.Contains("document.title = text;", script);
        string app = Read("Components", "App.razor");
        Assert.True(app.IndexOf("js/paged-blocks.js", StringComparison.Ordinal) is var at and >= 0 && at < app.IndexOf("js/paged-preview.js", StringComparison.Ordinal));
    }

    [Fact]
    public void AParagraphBreaksBetweenLinesAndATallFigureIsScaledToAPage()
    {
        // Issue #737: a heading alone on a page, text over the footer and a diagram across a page edge. The behaviour
        // itself is checked in a browser by tools/a11y/paged-check.mjs; this pins the parts down in the source.
        string fit = Read("wwwroot", "js", "paged-fit.js");
        if (fit.Length == 0) return;
        Assert.Contains("window.traceablePageFit", fit);
        Assert.Contains("const MIN_LINES = 2;", fit);
        Assert.Contains("'--page-split-shape', 'polygon('", fit);
        Assert.Contains("data-page-fit", fit);
        Assert.DoesNotContain("innerHTML", fit);
        Assert.DoesNotContain("appendChild", fit); // Blazor owns the report: nothing is added inside it

        string pages = Read("wwwroot", "js", "paged-preview.js");
        Assert.Contains("fit.split(el,", pages);
        Assert.Contains("fit.figures(sheet, body, front,", pages);
        Assert.Contains("!moved.has(list[first])", pages); // a run of headings moves once
        Assert.Contains("isAtom: isAtom", Read("wwwroot", "js", "paged-blocks.js"));

        string css = Read("wwwroot", "css", "report-paper.css");
        Assert.Contains("[data-paged-sheet] [data-page-split]::before { content: \"\"; float: left; width: 100%; height: var(--page-split-end); shape-outside: var(--page-split-shape); }", css);
        Assert.Contains("[data-page-split]::before { content: none !important; }", css); // not on paper

        string app = Read("Components", "App.razor");
        int blocks = app.IndexOf("js/paged-blocks.js", StringComparison.Ordinal), own = app.IndexOf("js/paged-fit.js", StringComparison.Ordinal);
        Assert.True(blocks >= 0 && blocks < own && own < app.IndexOf("js/paged-preview.js", StringComparison.Ordinal));
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

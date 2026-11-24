using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Modules.Multivocal.Pages;

// The paper page's state: the run's files, the open citation, and stepping through the citations to look at.
public partial class MultivocalPaperPage
{
    [Parameter] public Guid RunId { get; set; }

    private MultivocalReportInput? input;
    private MultivocalPaperFile? paper;
    private MultivocalCitationAudit? audit;
    private IReadOnlyList<GreyReference> references = Array.Empty<GreyReference>();
    private IReadOnlyList<CitationSupportResult> checks = Array.Empty<CitationSupportResult>();
    private (string Field, int Sentence, int Reference)? openCitation;
    private int attentionIndex = -1;
    private string? pendingFocus;

    protected override void OnParametersSet()
    {
        var module = ReviewModules.Find("multivocal")!;
        if (!ModuleOptions.CanOpen(module)) { Nav.NotFound(); return; }
        if (ReviewModules.ElsewhereFor(Runs.LoadHeader(RunId), module.Key) is string elsewhere)
        {
            Nav.NavigateTo(elsewhere, replace: true);
            return;
        }
        input = Reporter.LoadInput(RunId);
        paper = Writer.Load(RunId);
        audit = Writer.LoadAudit(RunId);
        checks = audit?.SupportChecks ?? new List<CitationSupportResult>();
        references = input == null ? Array.Empty<GreyReference>() : GreyReferences.Build(input.Ledger, input.Pages, input.Extraction);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (pendingFocus is { } focusId)
        {
            pendingFocus = null;
            try { await JS.InvokeAsync<bool>("traceableReview.focus", focusId); }
            catch (JSException) { /* moving focus is a convenience; the page works without it */ }
            catch (JSDisconnectedException) { /* the tab was closed */ }
        }
    }

    // The reading order of the paper's fields, for stepping through the citations that need attention.
    private int FieldRank(string field)
    {
        if (field == MultivocalWriter.RationaleField) return -1;
        int result = paper?.Results.FindIndex(r => r.Field == field) ?? -1;
        if (result >= 0) return 1 + result;
        return field == MultivocalWriter.DiscussionField ? 2000 : 3000;
    }

    private List<CitationSupportResult> Attention => CitationView.Attention(checks, FieldRank);

    private List<OutlineEntry> Outline => ReportOutline.Multivocal(paper!);

    // The line under the title: what the review rests on and what was checked, all counted in code.
    private string RunLine
    {
        get
        {
            var flow = MultivocalPaperParts.Flow(input!);
            return $"{flow.Extracted} sources · {flow.Found} found · {flow.Themes} themes · {checks.Count} citations checked";
        }
    }

    /// <summary>
    /// The heading of the citation-check card: the citations checked, the same number as the line under the title and the
    /// check note, and the removed ones on their own (#467: the card said 1419 where the rest of the page said 1283).
    /// </summary>
    public static string CheckedHeading(VerdictTally counts)
    {
        int checkedCount = counts.Total - counts.Removed;
        string text = $"{checkedCount} {(checkedCount == 1 ? "citation" : "citations")} checked against the sources";
        return counts.Removed == 0 ? text : $"{text}, and {counts.Removed} removed";
    }

    // The citation-check panel (from 1500 px): which citations it steps through, and where it is.
    private string checkFilter = ReportOutline.Flagged;
    private int checkIndex = -1;

    private void SetCheckFilter(string filter)
    {
        checkFilter = filter;
        checkIndex = -1;
    }

    private async Task StepCheck()
    {
        var list = ReportOutline.InOrder(checks, checkFilter, FieldRank);
        if (list.Count == 0) return;
        checkIndex = (checkIndex + 1) % list.Count;
        var c = list[checkIndex];
        openCitation = (c.Field, c.SentenceIndex, c.Reference);
        StateHasChanged();
        try { await JS.InvokeAsync<bool>("traceableReview.scrollTo", CitationView.CiteId(c.Field, c.SentenceIndex, c.Reference)); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not scroll to the citation: {Message}", ex.Message);
        }
    }

    private async Task PrintPaper()
    {
        try { await JS.InvokeAsync<bool>("traceableReview.print"); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not open the print window: {Message}", ex.Message);
        }
    }

    private async Task StepAttention(int delta)
    {
        var list = Attention;
        if (list.Count == 0) return;
        attentionIndex = ((attentionIndex < 0 && delta < 0 ? 0 : attentionIndex) + delta + list.Count) % list.Count;
        var c = list[attentionIndex];
        openCitation = (c.Field, c.SentenceIndex, c.Reference);
        StateHasChanged();
        try { await JS.InvokeAsync<bool>("traceableReview.scrollTo", CitationView.CiteId(c.Field, c.SentenceIndex, c.Reference)); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Scrolling is a convenience: the citation is open and the bar shows which one it is, so the page works without it.
            Logger.LogDebug("Could not scroll to the citation: {Message}", ex.Message);
        }
    }

    private void ToggleCitation(string field, int sentence, int reference)
    {
        bool closing = openCitation == (field, sentence, reference);
        openCitation = closing ? null : (field, sentence, reference);
        pendingFocus = closing ? null : $"verdict-{field}";
    }

    private void CloseCitation()
    {
        if (openCitation is { } open) pendingFocus = CitationView.CiteId(open.Field, open.Sentence, open.Reference);
        openCitation = null;
    }

    private static string ReferenceLine(GreyReference r) => GreyReferences.Line(r).Replace("*", "");

    // Tier 0 is an academic source, which has no outlet tier.
    private static string Ordinal(int n) => n switch { 0 => "none (academic)", 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };
}

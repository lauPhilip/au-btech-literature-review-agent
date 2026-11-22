using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Pages;

// What the reader does on the report page: open and step through citations, notes, re-checks, copy, print and delete.
public partial class SpecMatrix
{
    internal bool confirmDelete;
    internal bool deleted;

    internal void DeleteRun()
    {
        confirmDelete = false;
        if (Runs.DeleteRun(State.CurrentSessionId, editKey))
        {
            deleted = true;
            State.CurrentSessionId = Guid.Empty;
            report = new PrismaReport();
        }
    }

    internal Dictionary<string, string> fullTextUrls = new();
    internal List<AuBtechReviewAgent.ReviewerNote> notes = new();
    internal string? editingNote;     // which note is open for editing: "study-3" or "citation-field-2-3"
    internal string noteDraft = "";
    internal string? noteMessage;

    internal static string? DoiUrl(string? doi) =>
        string.IsNullOrWhiteSpace(doi) ? null : "https://doi.org/" + Uri.EscapeDataString(doi.Trim()).Replace("%2F", "/");

    internal async Task SaveNoteAsync(string kind, int reference, string field, int sentence)
    {
        bool saved = await Runs.SaveNoteAsync(State.CurrentSessionId, editKey, kind, reference, noteDraft, field, sentence);
        if (saved)
        {
            notes = Runs.LoadNotes(State.CurrentSessionId).ToList();
            editingNote = null;
            noteMessage = null;
        }
        else
        {
            noteMessage = "The note could not be saved. Only the browser that started this run can add notes.";
        }
    }

    // Re-checking one citation after the run (PrismaReviewEngine.RecheckCitationAsync).
    internal List<AuBtechReviewAgent.CitationRecheck> rechecks = new();
    internal int rechecksLeft;
    internal bool rechecking;
    internal string? recheckMessage;
    internal bool recheckFailed;

    internal AuBtechReviewAgent.CitationRecheck? RecheckOf((string Field, int Sentence, int Reference) c) =>
        rechecks.LastOrDefault(r => r.Field == c.Field && r.SentenceIndex == c.Sentence && r.Reference == c.Reference);

    internal async Task RecheckAsync((string Field, int Sentence, int Reference) c)
    {
        if (rechecking) return;
        rechecking = true;
        recheckMessage = null;
        recheckFailed = false;
        try
        {
            var result = await ReviewEngine.RecheckCitationAsync(State.CurrentSessionId, c.Field, c.Sentence, c.Reference, editKey: editKey);
            await LoadAuditAsync(Runs.FolderOf(State.CurrentSessionId));
            recheckMessage = $"Checked again: {VerdictLabel(result.Verdict).ToLowerInvariant()}, against the {result.EvidenceBasis}.";
        }
        catch (AuBtechReviewAgent.PrismaReviewEngine.RecheckException ex)
        {
            recheckMessage = ex.Message;
            recheckFailed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            recheckMessage = "The check could not be run just now (the model or the paper's server did not answer). Try again later.";
            recheckFailed = true;
        }
        finally
        {
            rechecking = false;
        }
    }

    internal AuBtechReviewAgent.CitationSupportResult? FindCheck(string field, int sentence, int reference) =>
        auditChecks.FirstOrDefault(c => c.Field == field && c.SentenceIndex == sentence && c.Reference == reference);

    internal static string CiteId(string field, int sentence, int reference) => AuBtechReviewAgent.CitationView.CiteId(field, sentence, reference);

    // Citations that need a reader's attention (partly or not supported), in the order they appear in the report.
    internal List<AuBtechReviewAgent.CitationSupportResult> AttentionList =>
        AuBtechReviewAgent.CitationView.Attention(auditChecks, f => AuBtechReviewAgent.CitationView.FieldRank(f, report.SynthesisSections.Select(x => x.Field).ToList()));
    internal int attentionIndex = -1;

    internal async Task StepAttention(int delta)
    {
        var list = AttentionList;
        if (list.Count == 0) return;
        attentionIndex = ((attentionIndex < 0 && delta < 0 ? 0 : attentionIndex) + delta + list.Count) % list.Count;
        await OpenAndScrollAsync(list[attentionIndex]);
    }

    // The citation-check panel (from 1500 px): which citations it steps through, and where it is.
    internal string checkFilter = AuBtechReviewAgent.ReportOutline.Flagged;
    internal int checkIndex = -1;

    internal void SetCheckFilter(string filter)
    {
        checkFilter = filter;
        checkIndex = -1;
    }

    internal async Task StepCheck()
    {
        var list = AuBtechReviewAgent.ReportOutline.InOrder(auditChecks, checkFilter,
            f => AuBtechReviewAgent.CitationView.FieldRank(f, report.SynthesisSections.Select(x => x.Field).ToList()));
        if (list.Count == 0) return;
        checkIndex = (checkIndex + 1) % list.Count;
        await OpenAndScrollAsync(list[checkIndex]);
    }

    internal async Task OpenAndScrollAsync(AuBtechReviewAgent.CitationSupportResult c)
    {
        openCitation = (c.Field, c.SentenceIndex, c.Reference);
        StateHasChanged();
        try { await JS.InvokeAsync<bool>("traceableReview.scrollTo", CiteId(c.Field, c.SentenceIndex, c.Reference)); }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
    }

    /// <summary>Where a reader can open a source: its open-access copy, else its DOI, else its page (only http(s) addresses).</summary>
    internal string? SourceLink(int reference)
    {
        var row = tableRecords.FirstOrDefault(t => t.ReferenceNumber == reference);
        if (row == null) return null;
        if (fullTextUrls.TryGetValue(row.PaperId, out var pdf) && AuBtechReviewAgent.PrismaReviewEngine.IsWebAddress(pdf)) return pdf;
        return DoiUrl(row.Doi) ?? (AuBtechReviewAgent.PrismaReviewEngine.IsWebAddress(row.Url) ? row.Url : null);
    }

    // Element to focus after the next render: the verdict box when a citation opens, the number when it closes.
    internal string? pendingFocus;

    internal void ToggleCitation(string field, int sentence, int reference)
    {
        bool closing = openCitation == (field, sentence, reference);
        openCitation = closing ? null : (field, sentence, reference);
        pendingFocus = closing ? null : $"verdict-{field}";
    }

    // The statement on AI use for this run (section 6.4), and the confirmation after copying it.
    internal string aiStatement = "";
    internal string aiShort = "";
    internal List<string> researchQuestions = new();
    internal string? copyNotice;

    internal async Task CopyStatement(string text, string what)
    {
        bool copied = false;
        try { copied = await JS.InvokeAsync<bool>("traceableReview.copyText", text); }
        catch (JSException) { /* the browser refused the clipboard; the text stays on the page to select by hand */ }
        catch (JSDisconnectedException) { /* the tab was closed */ }
        copyNotice = copied ? $"{what} copied. Edit it to fit your venue's policy on AI use before you submit." : "Copying is not allowed here; select the text and copy it by hand.";
    }

    internal async Task PrintReport()
    {
        try { await JS.InvokeVoidAsync("traceableReview.print"); }
        catch (JSException) { /* printing is a convenience; Ctrl+P works too */ }
        catch (JSDisconnectedException) { /* the tab was closed */ }
    }

    internal void CloseCitation()
    {
        if (openCitation is { } open) pendingFocus = CiteId(open.Field, open.Sentence, open.Reference);
        openCitation = null;
    }

    internal static string VerdictLabel(string? verdict) => AuBtechReviewAgent.CitationView.VerdictLabel(verdict);
    internal static string VerdictText(string? verdict) => AuBtechReviewAgent.CitationView.VerdictText(verdict);

    // The run's edit key from this browser's storage: only the browser that started the run can change it.
    internal string? editKey;
    internal Guid editKeyFor;
    internal bool canEdit;
    internal bool linkCopied;

    internal async Task CopyLink()
    {
        try { linkCopied = await JS.InvokeAsync<bool>("traceableReview.copyLink"); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException) { linkCopied = false; }
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Components;
using AuBtechReviewAgent.Components.Paper;

namespace AuBtechReviewAgent.Components.Pages;

// The checked editor on the report page (issue #413): the run's owner switches it on, opens a sentence, and
// "Check and save" calls PrismaReviewEngine.EditReportSentenceAsync, which checks the new sentence's citations and
// keeps the edit in report-edits.json. The engine checks the edit key and every rule again; the page only shows.
public partial class SpecMatrix
{
    internal AuBtechReviewAgent.ReportEditLog editLog = new();
    internal bool editMode;
    internal (string Field, int Sentence)? editing;
    internal bool editChecking;
    internal string? editError;
    internal AuBtechReviewAgent.ReportEdit? editResult;
    internal bool undoing;
    internal string? undoMessage;
    internal bool undoFailed;

    /// <summary>The owner of a finished run may edit; nobody may while the run is going.</summary>
    internal bool CanEditText => canEdit && !ReviewEngine.IsRunActive(State.CurrentSessionId);

    internal string PageHref => $"/spec-matrix/{State.CurrentSessionId}";

    internal void ResetEdits(string folder)
    {
        editLog = AuBtechReviewAgent.ReportEditLog.Load(folder);
        editMode = false;
        editing = null;
        editChecking = undoing = undoFailed = false;
        editError = undoMessage = null;
        editResult = null;
    }

    internal void ToggleEditMode()
    {
        editMode = !editMode;
        if (!editMode) editing = null;
        editError = null;
        editResult = null;
    }

    internal void StartEdit(string field, int sentence)
    {
        if (!AuBtechReviewAgent.EditView.CanEdit(CanEditText, editMode, field) || editChecking) return;
        editing = (field, sentence);
        editError = null;
        editResult = null;
        openCitation = null;
        pendingFocus = SentenceEditor.TextId;
    }

    internal void CancelEdit()
    {
        if (editChecking) return;
        if (editing is { } e) pendingFocus = CitedProse.EditButtonId(e.Field, e.Sentence);
        editing = null;
        editError = null;
        editResult = null;
    }

    internal async Task SaveEditAsync(string text)
    {
        if (editing is not { } e || editChecking) return;
        // Only a sentence that cites a study costs model calls; those count against the per-address limit (#449).
        if (AuBtechReviewAgent.CitationSupportChecker.ReferencesIn(text ?? "").Count > 0
            && !Actions.TryTake(AuBtechReviewAgent.ActionLimiter.EditorCheck, PrerenderClientAddress ?? "unknown", out string limited))
        {
            editError = "Your sentence was not checked or saved. " + limited;
            return;
        }
        editChecking = true;
        editError = null;
        try
        {
            editResult = await ReviewEngine.EditReportSentenceAsync(State.CurrentSessionId, e.Field, e.Sentence, text, editKey: editKey);
            await ReloadEditsAsync();
        }
        catch (AuBtechReviewAgent.PrismaReviewEngine.ReportEditException ex)
        {
            editError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("An edit could not be checked: {Message}", ex.Message);
            editError = "The check could not be run just now, because the model did not answer. Your sentence is still in the box. Try again in a few minutes.";
        }
        finally
        {
            editChecking = false;
        }
    }

    internal async Task UndoAsync(AuBtechReviewAgent.ReportEdit edit)
    {
        if (undoing || editChecking) return;
        undoing = true;
        undoMessage = null;
        undoFailed = false;
        try
        {
            await ReviewEngine.UndoReportEditAsync(State.CurrentSessionId, edit.Field, edit.SentenceIndex, editKey);
            if (editing == (edit.Field, edit.SentenceIndex)) { editing = null; editResult = null; }
            await ReloadEditsAsync();
            undoMessage = $"Edit {edit.Number} was undone. The sentence reads as it did before it.";
        }
        catch (AuBtechReviewAgent.PrismaReviewEngine.ReportEditException ex)
        {
            undoMessage = ex.Message;
            undoFailed = true;
        }
        finally
        {
            undoing = false;
        }
    }

    /// <summary>Reads the run's report and edits again, so the text, the chips, the check paragraph and the statement on AI use match.</summary>
    private async Task ReloadEditsAsync()
    {
        string folder = Runs.FolderOf(State.CurrentSessionId);
        editLog = AuBtechReviewAgent.ReportEditLog.Load(folder);
        string reportPath = Path.Join(folder, "prisma-report.json");
        if (File.Exists(reportPath))
        {
            var raw = JsonSerializer.Deserialize<PrismaReport>(await File.ReadAllTextAsync(reportPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (raw != null) report = AuBtechReviewAgent.ReportEdits.Apply(raw, editLog);
        }
        await LoadAuditAsync(folder, reloadReport: false);
        if (ReviewEngine.LoadState(State.CurrentSessionId) is { } state)
            aiStatement = AuBtechReviewAgent.AiUseStatement.Methods(state, State.CurrentSessionId, editLog);
    }

    /// <summary>The edit box for the sentence that is open, if it is in this text.</summary>
    internal RenderFragment? EditorFor(string field) => editing is { } e && e.Field == field
        ? __builder =>
        {
            __builder.OpenComponent<SentenceEditor>(0);
            __builder.AddComponentParameter(1, nameof(SentenceEditor.Sentence), e.Sentence);
            __builder.AddComponentParameter(2, nameof(SentenceEditor.Label), AuBtechReviewAgent.EditView.FieldLabel(report, field));
            __builder.AddComponentParameter(3, nameof(SentenceEditor.Original), AuBtechReviewAgent.EditView.SentenceOf(report, field, e.Sentence) ?? "");
            __builder.AddComponentParameter(4, nameof(SentenceEditor.Checks), auditChecks.Where(c => c.Field == field && c.SentenceIndex == e.Sentence).ToList());
            __builder.AddComponentParameter(5, nameof(SentenceEditor.Checking), editChecking);
            __builder.AddComponentParameter(6, nameof(SentenceEditor.Error), editError);
            __builder.AddComponentParameter(7, nameof(SentenceEditor.Result), editResult);
            __builder.AddComponentParameter(8, nameof(SentenceEditor.UsedLine), AuBtechReviewAgent.EditView.UsedLine(editLog));
            __builder.AddComponentParameter(9, nameof(SentenceEditor.SourceTitle), (Func<int, string?>)SourceTitleOf);
            __builder.AddComponentParameter(10, nameof(SentenceEditor.OnSave), EventCallback.Factory.Create<string>(this, SaveEditAsync));
            __builder.AddComponentParameter(11, nameof(SentenceEditor.OnCancel), EventCallback.Factory.Create(this, CancelEdit));
            __builder.CloseComponent();
        }
        : null;

    internal string? SourceTitleOf(int reference) => tableRecords.FirstOrDefault(t => t.ReferenceNumber == reference)?.Title;
}

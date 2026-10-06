using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// A reviewer's own note on one citation or one included study, written on the Review Output page. Notes are the
/// reviewer's record of what they checked and decided; they are never sent to the model.
/// </summary>
public sealed record ReviewerNote(string Kind, int Reference, string Field, int SentenceIndex, string Text, DateTime UpdatedUtc)
{
    public const string Citation = "citation";
    public const string Study = "study";

    public bool IsFor(string kind, int reference, string field = "", int sentenceIndex = -1) =>
        Kind == kind && Reference == reference && Field == field && SentenceIndex == sentenceIndex;
}

/// <summary>
/// Reviewer notes are kept by <see cref="RunStore"/> in reviewer-notes.json in the run folder and included in the
/// run archive. These members pass through to it so existing callers keep working.
/// </summary>
public partial class PrismaReviewEngine
{
    public const string NotesFile = RunStore.NotesFile;

    /// <summary>Only http(s) addresses are linked from the report, so a source cannot inject a javascript: link.</summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public const int MaxNoteLength = RunStore.MaxNoteLength;
    public const int MaxNotesPerRun = RunStore.MaxNotesPerRun;

    public IReadOnlyList<ReviewerNote> LoadNotes(Guid runId) => Store.LoadNotes(runId);

    /// <summary>Saves, replaces or removes one note (see <see cref="RunStore.SaveNoteAsync"/>).</summary>
    public Task<bool> SaveNoteAsync(Guid runId, string? editKey, string kind, int reference, string text, string field = "", int sentenceIndex = -1) =>
        Store.SaveNoteAsync(runId, editKey, kind, reference, text, field, sentenceIndex);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
/// Reviewer notes, kept in reviewer-notes.json in the run folder and included in the run archive (and so in its
/// manifest). Only the browser that started the run can write them; anyone with the link can read them.
/// </summary>
public partial class PrismaReviewEngine
{
    public const string NotesFile = "reviewer-notes.json";

    /// <summary>Only http(s) addresses are linked from the report, so a source cannot inject a javascript: link.</summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public const int MaxNoteLength = 1000;
    public const int MaxNotesPerRun = 500;

    private static readonly SemaphoreSlim NotesGate = new(1, 1);

    public IReadOnlyList<ReviewerNote> LoadNotes(Guid runId)
    {
        string path = Path.Join(GetWorkspaceFolderPath(runId), NotesFile);
        if (!File.Exists(path)) return Array.Empty<ReviewerNote>();
        try { return JsonSerializer.Deserialize<List<ReviewerNote>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Run {RunId}: reviewer notes unreadable: {Message}", runId, ex.Message);
            return Array.Empty<ReviewerNote>();
        }
    }

    /// <summary>
    /// Saves, replaces or (with empty text) removes one note. Returns false without the run's edit key, for a run
    /// that does not exist or is still going, or when the run already has the maximum number of notes.
    /// </summary>
    public async Task<bool> SaveNoteAsync(Guid runId, string? editKey, string kind, int reference, string text, string field = "", int sentenceIndex = -1)
    {
        if (kind is not (ReviewerNote.Citation or ReviewerNote.Study) || reference < 1) return false;
        if (IsRunActive(runId) || !CanEdit(runId, editKey) || LoadState(runId) == null) return false;

        string clean = ReviewInputGuard.Normalize(text, singleLine: false);
        if (clean.Length > MaxNoteLength) clean = clean[..MaxNoteLength];
        if (kind == ReviewerNote.Study) { field = ""; sentenceIndex = -1; }

        await NotesGate.WaitAsync();
        try
        {
            var notes = LoadNotes(runId).Where(n => !n.IsFor(kind, reference, field, sentenceIndex)).ToList();
            if (clean.Length > 0)
            {
                if (notes.Count >= MaxNotesPerRun) return false;
                notes.Add(new ReviewerNote(kind, reference, field, sentenceIndex, clean, DateTime.UtcNow));
            }
            var ordered = notes.OrderBy(n => n.Kind, StringComparer.Ordinal).ThenBy(n => n.Reference)
                .ThenBy(n => n.Field, StringComparer.Ordinal).ThenBy(n => n.SentenceIndex).ToList();
            await SafeFile.WriteAllTextAsync(Path.Join(GetWorkspaceFolderPath(runId), NotesFile),
                JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        finally
        {
            NotesGate.Release();
        }
    }
}

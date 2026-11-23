using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The checked editor of a finished systematic report: the reviewer rewrites one sentence, its citations are checked
/// again against the source text kept from the run, and the edit is kept with the verdicts in report-edits.json. The
/// run's own files are never changed; the report page, main.tex and the API put the edits in place when they read
/// the report (<see cref="ReportEdits.Apply(PrismaReport, ReportEditLog?)"/>). Only the browser holding the run's
/// edit key can edit, as with notes and re-checks.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>Each edit costs one cheap model call per cited study on the server's key; a run gets this many edits.</summary>
    public const int MaxReportEditsPerRun = 100;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> EditLocks = new();

    public sealed class ReportEditException(string message) : Exception(message);

    /// <summary>Every edit of the run's report, in order (empty when there are none).</summary>
    public ReportEditLog LoadReportEdits(Guid runId) => ReportEditLog.Load(GetWorkspaceFolderPath(runId));

    /// <summary>
    /// Replaces one sentence of the report with the reviewer's text and checks every citation in it again. A citation
    /// that is not fully supported is kept and marked, like in the generated text. Throws
    /// <see cref="ReportEditException"/> with a plain reason when the edit is not allowed.
    /// </summary>
    public async Task<ReportEdit> EditReportSentenceAsync(Guid runId, string field, int sentenceIndex, string? text,
        UserApiKeys? userKeys = null, string? editKey = null)
    {
        var (gate, state, report, log) = await OpenForEditAsync(runId, editKey);
        try
        {
            if (log.Edits.Count(e => e.Action == ReportEdit.Edited) >= MaxReportEditsPerRun)
                throw new ReportEditException($"This run has used its {MaxReportEditsPerRun} edits.");
            string current = CurrentFieldText(report, log, field);
            int referenceCount = state.SynthesizedRecords.Count == 0 ? 0 : Math.Max(state.SynthesizedRecords.Count, state.SynthesizedRecords.Max(r => r.ReferenceNumber));
            var (sentence, error) = ReportEdits.Prepare(current, sentenceIndex, text, referenceCount);
            if (sentence == null) throw new ReportEditException(error!);
            var span = ReportEdits.SentenceSpans(current)[sentenceIndex];

            var references = CitationSupportChecker.ReferencesIn(sentence);
            var checks = new List<CitationSupportResult>();
            if (references.Count > 0)
            {
                string workspace = GetWorkspaceFolderPath(runId);
                var sources = new List<ReferencedPaper>();
                var findings = new Dictionary<int, IReadOnlyList<string>>();
                foreach (int reference in references)
                {
                    if (await KeptSourceAsync(state, workspace, reference) is { } source) sources.Add(source);
                    var extraction = state.Extractions?.FirstOrDefault(e => e.ReferenceNumber == reference);
                    if (extraction != null) findings[reference] = ThematicSynthesis.VerifiedFindings(extraction).Select(f => f.Finding.Quote).ToList();
                }
                var (activeMistral, _, _, _) = ResolveKeys(userKeys);
                IChatCompletionService chat = ChatFactory != null ? ChatFactory(activeMistral) : LlmFactory.CreateForReview(Llm, activeMistral);
                checks = await Grounding.CheckEditAsync(chat, new CitedSentence(field, sentenceIndex, sentence, references), sources, findings);
                // Every citation gets exactly one verdict on file, whatever the model answered.
                checks = references.Select(r => checks.FirstOrDefault(c => c.Reference == r) ?? new CitationSupportResult
                {
                    Field = field, SentenceIndex = sentenceIndex, Sentence = sentence, Reference = r,
                    Verdict = CitationSupportChecker.Unverifiable, EvidenceBasis = "none", Reason = "The check gave no verdict for this citation.",
                    AttributedText = CitationSupportChecker.AttributedClause(sentence, r),
                }).ToList();
            }

            var edit = new ReportEdit(log.Edits.Count + 1, ReportEdit.Edited, field, sentenceIndex, current.Substring(span.Start, span.Length),
                sentence, DateTime.UtcNow, EditorOf(runId), checks, references.Count > 0 ? Llm.DisplayNameFor("citation-check") : "",
                RecordingChatCompletionService.AppVersion.Split('+')[0]);
            log.Edits.Add(edit);
            await log.SaveAsync(GetWorkspaceFolderPath(runId));
            _log.LogInformation("Run {RunId}: sentence {Index} of {Field} edited; {Count} citations checked again", runId, sentenceIndex, field, checks.Count);
            return edit;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Takes back the newest edit of one sentence that is still in force. No model call: the sentence gets back the
    /// text and the verdicts it had before. The undo is kept as one more entry.
    /// </summary>
    public async Task<ReportEdit> UndoReportEditAsync(Guid runId, string field, int sentenceIndex, string? editKey = null)
    {
        var (gate, _, _, log) = await OpenForEditAsync(runId, editKey);
        try
        {
            var stack = log.Stack(field, sentenceIndex);
            if (stack.Count == 0) throw new ReportEditException("This sentence has no edit to undo.");
            var undone = stack[^1];
            var restored = stack.Count > 1 ? stack[^2].Checks : new List<CitationSupportResult>();
            var entry = new ReportEdit(log.Edits.Count + 1, ReportEdit.Undone, field, sentenceIndex, undone.After, undone.Before,
                DateTime.UtcNow, EditorOf(runId), restored, "", RecordingChatCompletionService.AppVersion.Split('+')[0], undone.Number);
            log.Edits.Add(entry);
            await log.SaveAsync(GetWorkspaceFolderPath(runId));
            _log.LogInformation("Run {RunId}: edit {Number} of {Field} undone", runId, undone.Number, field);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The checks every change of the report needs; returns the run's lock (held), ledger, report and edits.</summary>
    private async Task<(SemaphoreSlim Gate, ReviewState State, PrismaReport Report, ReportEditLog Log)> OpenForEditAsync(Guid runId, string? editKey)
    {
        if (!CanEdit(runId, editKey)) throw new ReportEditException("Only the browser that started this run can edit its report.");
        var gate = EditLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero)) throw new ReportEditException("Another edit of this report is being checked. Try again in a moment.");
        try
        {
            if (Coordinator.ActiveRunIds.Contains(runId)) throw new ReportEditException("The run is still going; its report can be edited once it has finished.");
            var state = LoadState(runId) ?? throw new ReportEditException("This run could not be found. It may have expired and been deleted.");
            if (state.Stats.ProcessingStage != StageComplete) throw new ReportEditException("Only the report of a finished run can be edited.");
            string path = GetReportFilePath(runId);
            PrismaReport? report = null;
            try { if (File.Exists(path)) report = JsonSerializer.Deserialize<PrismaReport>(await File.ReadAllTextAsync(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { report = null; }
            if (report == null) throw new ReportEditException("This run has no report to edit.");
            return (gate, state, report, LoadReportEdits(runId));
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>The field as it reads now, with the edits in force; refused for the parts written in code.</summary>
    private static string CurrentFieldText(PrismaReport report, ReportEditLog log, string field)
    {
        if (!ReportEdits.IsEditable(field))
            throw new ReportEditException("This part is written in code from the run's record and cannot be edited: the method, the counts, the flow diagram, the references and the statement on AI use stay as the run wrote them.");
        string text = ReportEdits.FieldText(report, field) ?? throw new ReportEditException("This part is not in the report.");
        var sentences = log.Current().Values.Where(e => e.Field == field).ToDictionary(e => e.SentenceIndex, e => e.After);
        return ReportEdits.Apply(text, sentences);
    }

    /// <summary>Who made an edit: the holder of the run's edit key, or anyone with the link for a run older than edit keys.</summary>
    private string EditorOf(Guid runId) => HasOwner(runId) ? "run owner (edit key)" : "anyone with the link (run made before edit keys)";

    /// <summary>
    /// The cited study as the run read it: the abstract from the ledger and, only when the run had the full text, that
    /// text read again from the copy kept in the run folder and cleaned as the run cleaned it. An abstract-only study
    /// stays abstract-only: unlike a re-check, an edit does not look for a new copy.
    /// </summary>
    private async Task<ReferencedPaper?> KeptSourceAsync(ReviewState state, string workspace, int reference)
    {
        var record = state.SynthesizedRecords.FirstOrDefault(r => r.ReferenceNumber == reference);
        if (record == null) return null;
        var screening = state.Phases.Screening.FirstOrDefault(l => l.PaperId == record.PaperId);
        var paper = new AcademicPaper(record.PaperId, record.Title, screening?.Abstract ?? "", record.Year > 0 ? record.Year.ToString() : "",
            record.Authors, record.VenueName, record.Doi, record.Url);
        IReadOnlyList<DocumentChunk> chunks = Array.Empty<DocumentChunk>();
        bool kept = state.FullTextSources.TryGetValue(record.PaperId, out var had) && !had.StartsWith("none", StringComparison.Ordinal);
        if (kept)
        {
            try
            {
                var fullText = FullTextFetcher != null
                    ? await FullTextFetcher(paper, workspace)
                    : await DocumentRAGUtility.IngestAndChunkPaperAsync(paper, workspace, OpenSources.ContactEmail);
                chunks = HiddenText.CleanChunks(fullText.Chunks).Chunks;
            }
            catch (Exception ex)
            {
                _log.LogInformation("Kept full text for an edit check not readable: {Message}", SanitizeLogMessage(ex.Message));
            }
        }
        return new ReferencedPaper(reference, record.PaperId, record.Title, paper.Abstract, chunks);
    }
}

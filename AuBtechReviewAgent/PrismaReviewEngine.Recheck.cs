using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>One re-check of a single citation after the run, as kept under Rechecks in citation-audit.json.</summary>
public sealed record CitationRecheck(
    string Field, int SentenceIndex, int Reference, DateTime CheckedUtc,
    string VerdictBefore, string VerdictAfter, string EvidenceBefore, string EvidenceAfter,
    string? Quote, bool QuoteVerified, string Reason, string Model, string AppVersion);

/// <summary>
/// Re-check of one partly or not supported citation after the run, on request from the report page. The paper's
/// full text is fetched now if the run only had the abstract (an open-access copy may have appeared since, or the
/// download failed during the run), and the same two-step check runs on that one citation. The new verdict replaces
/// the old one in citation-audit.json, the old one is kept under Rechecks with the time, and the run's counts are
/// updated. The run-metrics line written when the run finished is left as it was: it measures the run itself.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>Each re-check is a few model calls on the server's key; a run gets this many.</summary>
    public const int MaxRechecksPerRun = 5;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RecheckLocks = new();

    public sealed class RecheckException(string message) : Exception(message);

    /// <summary>How many re-checks a run has left.</summary>
    public int RechecksLeft(Guid runId)
    {
        try
        {
            string path = Path.Join(GetWorkspaceFolderPath(runId), "citation-audit.json");
            if (!File.Exists(path)) return 0;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return Math.Max(0, MaxRechecksPerRun - ((root?["Rechecks"] as JsonArray)?.Count ?? 0));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public async Task<CitationSupportResult> RecheckCitationAsync(Guid runId, string field, int sentenceIndex, int reference, UserApiKeys? userKeys = null)
    {
        var gate = RecheckLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero)) throw new RecheckException("A citation of this run is being checked again already. Try again in a moment.");
        try
        {
            if (Coordinator.ActiveRunIds.Contains(runId)) throw new RecheckException("The run is still going; citations can be checked again once it has finished.");
            var state = LoadState(runId) ?? throw new RecheckException("This run could not be found. It may have expired and been deleted.");
            if (state.Stats.ProcessingStage != StageComplete) throw new RecheckException("Only citations of a finished run can be checked again.");

            string workspace = GetWorkspaceFolderPath(runId);
            string auditPath = Path.Join(workspace, "citation-audit.json");
            if (!File.Exists(auditPath)) throw new RecheckException("This run has no citation check to repeat.");
            var audit = JsonNode.Parse(await File.ReadAllTextAsync(auditPath)) as JsonObject ?? throw new RecheckException("The citation audit could not be read.");
            var checks = audit["SupportChecks"]?.Deserialize<List<CitationSupportResult>>() ?? new();
            var rechecks = audit["Rechecks"] as JsonArray ?? new JsonArray();
            if (rechecks.Count >= MaxRechecksPerRun) throw new RecheckException($"This run has used its {MaxRechecksPerRun} re-checks.");

            int index = checks.FindIndex(c => c.Field == field && c.SentenceIndex == sentenceIndex && c.Reference == reference);
            if (index < 0) throw new RecheckException("This citation was not part of the automated check.");
            var before = checks[index];
            if (before.Verdict is not (CitationSupportChecker.Partial or CitationSupportChecker.NotSupported))
                throw new RecheckException("Only partly supported and not supported citations can be checked again.");

            var record = state.SynthesizedRecords.FirstOrDefault(r => r.ReferenceNumber == reference)
                ?? throw new RecheckException("The cited study could not be found in the run.");
            var log = state.Phases.Screening.FirstOrDefault(l => l.PaperId == record.PaperId);
            var paper = new AcademicPaper(record.PaperId, record.Title, log?.Abstract ?? "", record.Year > 0 ? record.Year.ToString() : "",
                record.Authors, record.VenueName, record.Doi, record.Url);

            // The full text: the copy downloaded during the run is reused; otherwise it is looked for again now.
            FullTextResult fullText;
            try
            {
                fullText = FullTextFetcher != null
                    ? await FullTextFetcher(paper, workspace)
                    : await DocumentRAGUtility.IngestAndChunkPaperAsync(paper, workspace, OpenSources.ContactEmail);
            }
            catch (Exception ex)
            {
                _log.LogInformation("Run {RunId}: full text for the re-check not available: {Message}", runId, SanitizeLogMessage(ex.Message));
                fullText = new FullTextResult(Array.Empty<DocumentChunk>(), "none", null);
            }
            if (fullText.Chunks.Count > 0 && (!state.FullTextSources.TryGetValue(record.PaperId, out var had) || had.StartsWith("none", StringComparison.Ordinal)))
                state.FullTextSources[record.PaperId] = $"{fullText.Source} (fetched for a re-check after the run)";

            var referenced = new ReferencedPaper(reference, record.PaperId, record.Title, paper.Abstract, fullText.Chunks);
            var extraction = state.Extractions?.FirstOrDefault(e => e.ReferenceNumber == reference);
            var findings = new Dictionary<int, IReadOnlyList<string>>();
            if (extraction != null) findings[reference] = ThematicSynthesis.VerifiedFindings(extraction).Select(f => f.Finding.Quote).ToList();

            var (activeMistral, _, _, _) = ResolveKeys(userKeys);
            IChatCompletionService chat = ChatFactory != null ? ChatFactory(activeMistral) : LlmFactory.Create(Llm, activeMistral);
            var cited = new CitedSentence(field, sentenceIndex, before.Sentence, new[] { reference });
            List<CitationSupportResult> result;
            using (LlmStage.Begin("citation-recheck"))
                result = await CitationSupportChecker.CheckAsync(chat, new[] { cited }, new[] { referenced },
                    excerptsPerReference: 8, verifiedFindings: findings, parallelism: 1, secondCheck: true);
            var after = result.FirstOrDefault() ?? throw new RecheckException("The check gave no answer. Try again later.");
            after.ThemeNote = before.ThemeNote;
            checks[index] = after;

            var entry = new CitationRecheck(field, sentenceIndex, reference, DateTime.UtcNow, before.Verdict, after.Verdict,
                before.EvidenceBasis, after.EvidenceBasis, after.Quote, after.QuoteVerified, after.Reason, Llm.DisplayName,
                RecordingChatCompletionService.AppVersion.Split('+')[0]);
            rechecks.Add(JsonSerializer.SerializeToNode(entry));
            var oldSummary = CitationSupportSummary.From(checks.Select((c, i) => i == index ? before : c));
            var summary = CitationSupportSummary.From(checks);
            audit["SupportChecks"] = JsonSerializer.SerializeToNode(checks);
            audit["SupportSummary"] = JsonSerializer.SerializeToNode(summary);
            audit["Rechecks"] = rechecks;
            await SafeFile.WriteAllTextAsync(auditPath, audit.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            state.Stats.CitationsChecked = summary.Checked;
            state.Stats.CitationsSupported = summary.Supported;
            state.Stats.CitationsPartiallySupported = summary.PartiallySupported;
            state.Stats.CitationsNotSupported = summary.NotSupported;
            state.Stats.CitationsUnverifiable = summary.Unverifiable;
            state.Stats.CitationsInsufficientEvidence = summary.InsufficientEvidence;
            await SaveStateAsync(runId, state);
            await UpdateReportCheckSentenceAsync(workspace, oldSummary, summary, rechecks.Count);

            _log.LogInformation("Run {RunId}: citation [{Reference}] in {Field} checked again: {Before} -> {After}", runId, reference, field, before.Verdict, after.Verdict);
            return after;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The citation-check sentence in prisma-report.json gets the new counts and a note that citations were checked
    /// again after the run, so the report and the audit agree.
    /// </summary>
    private async Task UpdateReportCheckSentenceAsync(string workspace, CitationSupportSummary before, CitationSupportSummary after, int rechecks)
    {
        string path = Path.Join(workspace, "prisma-report.json");
        try
        {
            if (!File.Exists(path)) return;
            if (JsonNode.Parse(await File.ReadAllTextAsync(path)) is not JsonObject report) return;
            string sentence = report["CitationCheckSummary"]?.GetValue<string>() ?? "";
            string note = $" {rechecks} citation{(rechecks == 1 ? " was" : "s were")} checked again on request after the run (listed under Rechecks in citation-audit.json).";
            sentence = System.Text.RegularExpressions.Regex.Replace(sentence, @" \d+ citations? (was|were) checked again on request after the run \(listed under Rechecks in citation-audit\.json\)\.", "");
            string oldText = before.ToSentence(), newText = after.ToSentence();
            sentence = sentence.Contains(oldText, StringComparison.Ordinal) ? sentence.Replace(oldText, newText, StringComparison.Ordinal) : newText + " " + sentence;
            report["CitationCheckSummary"] = sentence.TrimEnd() + note;
            await SafeFile.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not update the citation-check sentence after a re-check: {Message}", SanitizeLogMessage(ex.Message));
        }
    }
}

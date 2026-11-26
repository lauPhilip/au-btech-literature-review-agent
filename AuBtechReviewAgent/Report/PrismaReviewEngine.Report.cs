using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;
/// <summary>Synthesis stage: full texts, extraction and appraisal, outline, cited sections, peer review and the report.</summary>
public partial class PrismaReviewEngine
{

    /// <summary>Test hook: replaces the full-text download (default: arXiv, the source's open-access link, then Unpaywall).</summary>
    public Func<AcademicPaper, string, Task<FullTextResult>>? FullTextFetcher { get; init; }

    /// <summary>
    /// Downloads and chunks the full text of every included paper, a few at a time. Where each text came
    /// from (or why there is none) is written to the ledger.
    /// </summary>
    private async Task RetrieveFullTextsAsync(RunContext ctx)
    {
        var included = ctx.State.Phases.Screening
            .Where(l => l.Decision == "Included" && ctx.Papers.ContainsKey(l.PaperId))
            .Select(l => ctx.Papers[l.PaperId])
            .ToList();

        using var throttle = new SemaphoreSlim(4);
        var downloads = included.Select(async paper =>
        {
            await throttle.WaitAsync();
            try
            {
                return FullTextFetcher != null
                    ? await FullTextFetcher(paper, ctx.Workspace)
                    : await DocumentRAGUtility.IngestAndChunkPaperAsync(paper, ctx.Workspace, OpenSources.ContactEmail);
            }
            catch (Exception ex)
            {
                return new FullTextResult(Array.Empty<DocumentChunk>(), $"none (download failed: {SanitizeLogMessage(ex.Message)})", null);
            }
            finally { throttle.Release(); }
        }).ToList();

        ReportProgress(ctx.RunId, ctx.State, RunProgress.FullText, 0, $"Fetching open-access full texts of {included.Count} studies");
        for (int i = 0; i < included.Count; i++)
        {
            // The live line names the paper waited for now ("Reading the full text of Doe et al., 2025"), built in code.
            ReportProgress(ctx.RunId, ctx.State, RunProgress.FullText, (double)i / included.Count, Of(i, included.Count, "papers"),
                LiveActivity.ShortCite(included[i].Authors, included[i].PublishedDate, included[i].Title));
            var result = await downloads[i];
            ReportProgress(ctx.RunId, ctx.State, RunProgress.FullText, (double)(i + 1) / included.Count, Of(i + 1, included.Count, "papers"));
            // Text a reader never sees is taken out before anything reads it (#369); what was removed is on file below.
            var (chunks, removed, hidden) = HiddenText.CleanChunks(result.Chunks);
            ctx.Chunks[included[i].Id] = chunks;
            ctx.State.FullTextSources[included[i].Id] = result.Source;
            if (IsWebAddress(result.Url)) ctx.State.FullTextUrls[included[i].Id] = result.Url!;
            if (result.Chunks.Count > 0) ctx.State.Stats.FullTextRetrieved++;
            ctx.State.FullTextFlags?.RemoveAll(f => f.PaperId == included[i].Id);
            var flags = HiddenText.Flags(string.Join("\n", chunks.Select(c => c.Text)), hidden);
            if (flags != null || removed != null)
                (ctx.State.FullTextFlags ??= new()).Add(new SourceTextFlags(included[i].Id, included[i].Title, flags ?? new(), removed, PromptSafety.ScanVersion));
        }
        await PublishAsync(ctx);
    }

    /// <summary>
    /// Structured data extraction and MMAT appraisal for every included study, a few at a time. Written to
    /// extraction.json and the ledger; each value carries a quote that was checked against the paper's text.
    /// </summary>
    private async Task ExtractStudiesAsync(RunContext ctx, IReadOnlyList<ReferencedPaper> papers)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.QualityParallelism));
        int extracted = 0;
        ReportProgress(ctx.RunId, ctx.State, RunProgress.Extraction, 0, $"Extracting and appraising {papers.Count} studies");
        // With research questions the extraction looks for what answers them (issue #430); every run reuses an identical earlier extraction.
        var focus = new ExtractionFocus(ctx.State.ResearchQuestions, ctx.Request.Objective, Cache, Llm.ModelFor("extraction"));
        var work = papers.Select(async p =>
        {
            await throttle.WaitAsync();
            try
            {
                StudyExtraction result;
                using (LlmStage.Begin("extraction"))
                    result = await StudyExtractor.ExtractAsync(ctx.Chat, p, focus: focus);
                int n = Interlocked.Increment(ref extracted);
                ReportProgress(ctx.RunId, ctx.State, RunProgress.Extraction, (double)n / papers.Count, Of(n, papers.Count, "studies"));
                return result;
            }
            finally { throttle.Release(); }
        }).ToList();
        ctx.State.Extractions = (await Task.WhenAll(work)).OrderBy(e => e.ReferenceNumber).ToList();
        try
        {
            await File.WriteAllTextAsync(Path.Join(ctx.Workspace, "extraction.json"),
                JsonSerializer.Serialize(ctx.State.Extractions, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning("Run {RunId}: extraction.json not written: {Message}", ctx.RunId, ex.Message); }
        await SaveStateAsync(ctx.RunId, ctx.State);
    }

    private async Task SynthesizeAsync(RunContext ctx)
    {
        var reviewState = ctx.State;
        var request = ctx.Request;

        // Gaps in the references are filled before they are numbered, since a fuller author list can change the order.
        await EnrichReferencesAsync(ctx);

        // One ordering for the whole report. The reference list shown to the model, the [n] markers it
        // writes, the extraction table and the LaTeX bibliography are all numbered from this list.
        var includedPapers = reviewState.Phases.Screening
            .Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ApaCitation ?? "", ApaCitationBuilder.ReferenceOrder)
            .ThenBy(p => p.PaperId, StringComparer.Ordinal)
            .ToList();

        reviewState.SynthesizedRecords.Clear();
        var referencedPapers = new List<ReferencedPaper>();
        for (int i = 0; i < includedPapers.Count; i++)
        {
            var log = includedPapers[i];
            var row = BuildRecord(log, i + 1);
            row.IntegrityNote = RetractionCheckText.Note(reviewState.RetractionCheck, log.PaperId);
            reviewState.SynthesizedRecords.Add(row);

            ctx.Papers.TryGetValue(log.PaperId, out var paper);
            ctx.Chunks.TryGetValue(log.PaperId, out var chunks);
            referencedPapers.Add(new ReferencedPaper(i + 1, log.PaperId, log.Title,
                paper?.Abstract ?? log.Abstract ?? "", (IReadOnlyList<DocumentChunk>?)chunks ?? Array.Empty<DocumentChunk>()));
        }
        await SaveStateAsync(ctx.RunId, reviewState);

        await ExtractStudiesAsync(ctx, referencedPapers);

        var sbReferences = new StringBuilder();
        for (int i = 0; i < includedPapers.Count; i++)
        {
            sbReferences.AppendLine($"[{i + 1}] - {includedPapers[i].ApaCitation}");
        }

        // About two excerpts per study (at least 40, at most 120), so a large review does not leave most
        // studies with a single excerpt or none.
        string groundedContext = GroundingContextBuilder.Build(referencedPapers, $"{request.Query} {request.Objective}",
            Math.Clamp(2 * referencedPapers.Count, 40, 120));

        await GeneratePrismaChecklistReportWithRAGAsync(ctx.RunId, ctx.Chat, request.Query, request.Objective,
            inc: request.Inclusion, exc: request.Exclusion, groundedContext, sbReferences.ToString(), reviewState,
            reviewState.SearchPerspectives, referenceCount: includedPapers.Count, referencedPapers);
    }

    private static IncludedPaperMetricRow BuildRecord(ScreeningLog log, int referenceNumber)
    {
        // Venue type, venue name and year come from the source metadata (ApaCitationBuilder). Runs from
        // before that change only have the citation string, so fall back to parsing it.
        string venue = !string.IsNullOrEmpty(log.VenueType) ? log.VenueType : ClassifyVenueFromCitation(log.ApaCitation);
        int parsedYear = log.Year > 0 ? log.Year : ExtractYearFromCitation(log.ApaCitation);

        var (cleanCategory, platformFull) = SourceOfPaper(log.PaperId);

        // Quartile only from an exact Scimago title match; no default.
        string quartile = "N/A";
        if (venue is "Journals" or "Transactions" && !string.IsNullOrWhiteSpace(log.VenueName))
        {
            quartile = JournalRankingMatcher.LookupQuartile(log.VenueName, parsedYear);
        }


        return new IncludedPaperMetricRow
        {
            Title = log.Title,
            Summary = log.BriefSummary,
            ApaCitation = log.ApaCitation ?? "",
            SourcePlatform = platformFull,
            Year = parsedYear,
            VenueType = venue,
            InclusionRationale = log.HumanReviewed && log.ModelDecision != null
                ? $"Included by the human reviewer (model decision: {log.ModelDecision}).{(log.HumanNote != null ? " Note: " + log.HumanNote : "")}"
                : log.Reasoning,
            Category = cleanCategory,
            Quartile = quartile,
            ConferenceRating = "N/A", // no conference-ranking data source is wired in, so no rating is claimed
            ReferenceNumber = referenceNumber,
            PaperId = log.PaperId,
            Authors = log.Authors ?? new List<string>(),
            VenueName = log.VenueName,
            Doi = log.Doi,
            Url = log.Url,
            Details = log.Details,
        };
    }

    /// <summary>The thematic synthesis, or a result that names why it failed; never throws, so it can run beside other steps.</summary>
    private async Task<ThematicResult> ThematicOrFallbackAsync(Guid sessionId, IChatCompletionService chat, string query, string explicitObjective,
        ReviewState finalState, IReadOnlyList<ReferencedPaper> papers, int referenceCount)
    {
        try
        {
            return await RunThematicSynthesisAsync(sessionId, chat, query, explicitObjective, finalState, papers, referenceCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Thematic synthesis failed, falling back to the single-pass synthesis: {Message}", SanitizeLogMessage(ex.Message));
            return new ThematicResult { Codebook = new ThematicCodebook { FallbackReason = $"the thematic synthesis failed ({SanitizeLogMessage(ex.Message)})" } };
        }
    }
}

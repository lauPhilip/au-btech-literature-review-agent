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
            var result = await downloads[i];
            ReportProgress(ctx.RunId, ctx.State, RunProgress.FullText, (double)(i + 1) / included.Count, Of(i + 1, included.Count, "papers"));
            ctx.Chunks[included[i].Id] = result.Chunks.ToList();
            ctx.State.FullTextSources[included[i].Id] = result.Source;
            if (IsWebAddress(result.Url)) ctx.State.FullTextUrls[included[i].Id] = result.Url!;
            if (result.Chunks.Count > 0) ctx.State.Stats.FullTextRetrieved++;
        }
        await PublishAsync(ctx);
    }

    /// <summary>
    /// Structured data extraction and MMAT appraisal for every included study, a few at a time. Written to
    /// extraction.json and the ledger; each value carries a quote that was checked against the paper's text.
    /// </summary>
    private async Task ExtractStudiesAsync(RunContext ctx, IReadOnlyList<ReferencedPaper> papers)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        int extracted = 0;
        ReportProgress(ctx.RunId, ctx.State, RunProgress.Extraction, 0, $"Extracting and appraising {papers.Count} studies");
        var work = papers.Select(async p =>
        {
            await throttle.WaitAsync();
            try
            {
                StudyExtraction result;
                using (LlmStage.Begin("extraction"))
                    result = await StudyExtractor.ExtractAsync(ctx.Chat, p);
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
            reviewState.SynthesizedRecords.Add(BuildRecord(log, i + 1));

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
        };
    }

    /// <summary>
    /// STORM-style outline-before-prose stage. Asks the model to identify a handful of themes and, per
    /// theme, the specific claims the grounded chunks actually support along with which reference numbers
    /// back each claim - so the final synthesis prose is written against a pre-checked claim map rather
    /// than generating everything in one uncontrolled pass. Returns an empty string (never throws) on any
    /// failure, so the calling report generation falls back to grounding directly on the raw chunks.
    /// </summary>
    private async Task<string> GenerateGroundedOutlineAsync(IChatCompletionService chat, string query, string explicitObjective, string groundedChunksText, string referenceListMapping)
    {
        if (string.IsNullOrWhiteSpace(groundedChunksText)) return string.Empty;

        var outlinePrompt = $$"""
            You are preparing a grounded synthesis outline for a systematic literature review: before writing prose, map out the specific claims the literature supports and which sources back each claim.

            REVIEW OBJECTIVE: "{{explicitObjective}}"
            PRIMARY TOPIC: "{{query}}"
            {{PromptSafety.ReviewerInputNotice}}

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            {{PromptSafety.DataOnlyNotice}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{PromptSafety.Wrap(groundedChunksText, "excerpts from the included papers")}}

            TASK: Identify 3 to 6 distinct themes that emerge across the included sources. For each theme, list 1-3 concrete, specific claims that the grounded chunks actually support, and for each claim list which reference numbers from the list above support it. Only use reference numbers that appear in the list above. If the grounded chunks are too sparse to support a claim, omit it rather than inventing one.

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "themes": [ { "theme": "short theme label", "claims": [ { "text": "specific claim grounded in the sources", "refs": [1, 3] } ] } ] }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<OutlineAnswer>(chat, outlinePrompt, JsonMode(0.2), a =>
                a.Themes == null || a.Themes.Count == 0 ? "themes must be a non-empty list." :
                a.Themes.Any(t => t.Claims == null) ? "every theme needs a claims list." : null);

            var sb = new StringBuilder();
            foreach (var theme in answer.Themes!.Where(t => !string.IsNullOrWhiteSpace(t.Theme)))
            {
                sb.AppendLine($"- Theme: {theme.Theme}");
                foreach (var claim in theme.Claims!.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
                    sb.AppendLine($"    * {claim.Text} {string.Join("", (claim.Refs ?? new()).Select(r => $"[{r}]"))}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Outline stage skipped: {Message}", SanitizeLogMessage(ex.Message));
            return string.Empty;
        }
    }

    private sealed class OutlineAnswer { public List<OutlineTheme>? Themes { get; set; } }
    private sealed class OutlineTheme { public string Theme { get; set; } = ""; public List<OutlineClaim>? Claims { get; set; } }
    private sealed class OutlineClaim { public string Text { get; set; } = ""; public List<int>? Refs { get; set; } }
    private sealed class SectionsAnswer { public string? Synthesis { get; set; } public string? Discussion { get; set; } }
    private sealed class CritiqueAnswer { public List<CritiqueComment>? Comments { get; set; } }
    private sealed class CritiqueComment { public string Section { get; set; } = ""; public string Severity { get; set; } = ""; public string Issue { get; set; } = ""; public string Suggestion { get; set; } = ""; }

    // Dedicated, citation-mandatory generation of the two sections that must carry inline citations:
    // Results & Synthesis (Item 20a) and Discussion (Item 23a). Generating them on their own - instead of
    // as two fields buried in the big multi-field JSON call, and without the generic stylistic refiner that
    // drops [n] markers - is what keeps the citations present and correct. The grounded claim-to-source
    // outline is fed in so every specific claim can be tied to a real reference number.
    private async Task<(string Synthesis, string Discussion)> GenerateCitedSectionsAsync(
        IChatCompletionService chat, string query, string explicitObjective,
        string groundedOutline, string referenceListMapping, string groundedChunksText, int referenceCount = 0)
    {
        // Length grows with the number of included studies: six paragraphs cannot cite seventy papers meaningfully.
        int synthesisParagraphs = Math.Clamp((int)Math.Ceiling(referenceCount / 6.0), 3, 8);
        int discussionParagraphs = Math.Clamp(2 + referenceCount / 15, 3, 5);
        var prompt = $$"""
            You are an expert systematic-review author writing two sections of a PRISMA 2020 review:
            the Results & Synthesis section (Item 20a) and the Discussion section (Item 23a).

            REVIEW OBJECTIVE: "{{explicitObjective}}"
            PRIMARY TOPIC: "{{query}}"
            {{PromptSafety.ReviewerInputNotice}}

            OFFICIAL ALPHABETIZED REFERENCE LIST (cite ONLY these numbers):
            {{referenceListMapping}}

            PRE-CHECKED CLAIM-TO-SOURCE OUTLINE (each claim already mapped to supporting reference numbers):
            {{(string.IsNullOrWhiteSpace(groundedOutline) ? "(No outline available - ground your writing directly on the source context chunks below.)" : groundedOutline)}}

            {{PromptSafety.DataOnlyNotice}}

            GROUNDED SOURCE CONTEXT CHUNKS:
            {{PromptSafety.Wrap(groundedChunksText, "excerpts from the included papers")}}

            REQUIREMENTS:
            1. Depth. For the synthesis, write about {{synthesisParagraphs}} substantial paragraphs that group findings
               by theme, compare and contrast what different sources report, and name agreements, tensions, and
               gaps. Draw on as many of the included sources as the context supports: every source whose
               excerpts bear on a theme should be cited where it fits. For the discussion, write about
               {{discussionParagraphs}} paragraphs interpreting what the findings mean, their limitations, and their
               implications - grounded in the same sources, not new claims.
            2. MANDATORY INLINE CITATIONS. Every sentence that states a specific finding, comparison, or claim
               drawn from the literature MUST end with an inline citation marker in square brackets that
               references the reference list by number, for example "...separating inference from governance [3]."
               or "...reported in both [2] and [5]." Use ONLY numbers that appear in the reference list above;
               never invent a number. Purely general framing sentences need no marker, but most sentences in
               these two sections should carry at least one citation.
            3. No placeholder venue names, no fabricated statistics. Ground every claim in the outline and
               context above.

            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "synthesis": "the full Results and Synthesis text with inline [n] citations", "discussion": "the full Discussion text with inline [n] citations" }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<SectionsAnswer>(chat, prompt, JsonMode(0.3), a =>
                string.IsNullOrWhiteSpace(a.Synthesis) || string.IsNullOrWhiteSpace(a.Discussion) ? "both synthesis and discussion must be non-empty text." : null);
            return (answer.Synthesis!, answer.Discussion!);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cited sections: falling back to the base draft: {Message}", SanitizeLogMessage(ex.Message));
            return (string.Empty, string.Empty);
        }
    }

    // Documented "peer reviewer" pass. An LLM reviews the synthesis and discussion for depth, citation
    // coverage, grounding, coherence, and over-claiming, then a second pass revises the two sections in
    // response to that feedback. Both the comments and the before/after text are returned so the whole
    // exchange can be written to peer-review-feedback.json for transparency. A single round keeps the
    // cost and latency bounded.
    private async Task<PeerReviewLog> PeerReviewAndReviseAsync(
        IChatCompletionService chat, string synthesis, string discussion,
        string referenceListMapping, string groundedOutline)
    {
        var log = new PeerReviewLog
        {
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            SynthesisBefore = synthesis,
            SynthesisAfter = synthesis,
            DiscussionBefore = discussion,
            DiscussionAfter = discussion,
            Verdict = "No changes applied"
        };

        if (string.IsNullOrWhiteSpace(synthesis) && string.IsNullOrWhiteSpace(discussion))
            return log;

        // 1. CRITIQUE
        var critiquePrompt = $$"""
            You are a strict but constructive peer reviewer for a systematic literature review. Review the
            two sections below for: (a) depth and specificity, (b) whether each specific claim carries an
            inline [n] citation, (c) whether cited numbers stay within the reference list, (d) coherence and
            logical flow, and (e) over-claiming beyond what the sources support.

            REFERENCE LIST (valid citation numbers):
            {{referenceListMapping}}

            CLAIM-TO-SOURCE OUTLINE:
            {{(string.IsNullOrWhiteSpace(groundedOutline) ? "(not available)" : groundedOutline)}}

            RESULTS AND SYNTHESIS (Item 20a):
            {{synthesis}}

            DISCUSSION (Item 23a):
            {{discussion}}

            Respond ONLY with a valid minified JSON object:
            { "comments": [ { "section": "synthesis or discussion", "severity": "high, medium, or low", "issue": "what is wrong or weak", "suggestion": "how to fix it" } ] }
            If a section is already strong, return few or no comments for it.
            """;

        var comments = new List<PeerReviewComment>();
        try
        {
            var answer = await LlmJson.GetAsync<CritiqueAnswer>(chat, critiquePrompt, JsonMode(0.2), a =>
                a.Comments == null ? "comments must be a list (it may be empty)." :
                a.Comments.Any(c => !LlmJson.OneOf(c.Section, "synthesis", "discussion")) ? "each comment's section must be \"synthesis\" or \"discussion\"." : null);
            comments = answer.Comments!.Select(c => new PeerReviewComment(c.Section, c.Severity, c.Issue, c.Suggestion)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Peer-review critique skipped: {Message}", SanitizeLogMessage(ex.Message));
        }

        log.Comments = comments;

        if (comments.Count == 0)
        {
            log.Verdict = "No revisions needed - reviewer found no actionable issues";
            return log;
        }

        // 2. REVISE in response to the feedback
        string feedbackText = string.Join("\n", comments.Select(c => $"- [{c.Severity}] ({c.Section}) {c.Issue} -> {c.Suggestion}"));
        var revisePrompt = $$"""
            You are the author revising two sections of a systematic review in response to peer-review
            feedback. Apply the feedback to improve depth and citation coverage. Keep every valid inline [n]
            citation, add the citations the feedback asks for using ONLY numbers from the reference list, and
            do not invent numbers or fabricate claims.

            REFERENCE LIST (valid citation numbers):
            {{referenceListMapping}}

            PEER-REVIEW FEEDBACK:
            {{feedbackText}}

            CURRENT RESULTS AND SYNTHESIS:
            {{synthesis}}

            CURRENT DISCUSSION:
            {{discussion}}

            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            { "synthesis": "the revised synthesis with inline [n] citations", "discussion": "the revised discussion with inline [n] citations" }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<SectionsAnswer>(chat, revisePrompt, JsonMode(0.3), a =>
                string.IsNullOrWhiteSpace(a.Synthesis) && string.IsNullOrWhiteSpace(a.Discussion) ? "synthesis and discussion must not both be empty." : null);
            if (!string.IsNullOrWhiteSpace(answer.Synthesis)) log.SynthesisAfter = answer.Synthesis;
            if (!string.IsNullOrWhiteSpace(answer.Discussion)) log.DiscussionAfter = answer.Discussion;
            log.Verdict = $"Revisions applied in response to {comments.Count} reviewer comment(s)";
        }
        catch (Exception ex)
        {
            _log.LogWarning("Peer-review revision skipped: {Message}", SanitizeLogMessage(ex.Message));
            log.Verdict = "Reviewer produced comments but the revision step failed; original text retained";
        }

        return log;
    }

    private async Task GeneratePrismaChecklistReportWithRAGAsync(Guid sessionId, IChatCompletionService chat, string query, string explicitObjective, string inc, string exc, string groundedChunksText, string referenceListMapping, ReviewState finalState, List<string>? searchPerspectives = null, int referenceCount = 0, IReadOnlyList<ReferencedPaper>? referencedPapers = null)
    {
        var effectivePerspectives = searchPerspectives ?? new List<string> { query };
        string supplementaryPerspectives = effectivePerspectives.Count > 1
            ? string.Join(" | ", effectivePerspectives.Skip(1))
            : "(none - single literal query only)";

        // STORM-style outline stage: before writing prose, ask the model to map out which specific,
        // grounded claims the sources actually support and which reference numbers back each one. This
        // is persisted to disk as its own audit artifact and then fed back in below so synthesisResultsItem
        // and discussionItem are written against a pre-checked claim map instead of free-associating.
        string groundedOutline;
        ReportProgress(sessionId, finalState, RunProgress.Synthesis, 0, "Outline and report draft");
        using (LlmStage.Begin("outline"))
            groundedOutline = await GenerateGroundedOutlineAsync(chat, query, explicitObjective, groundedChunksText, referenceListMapping);
        if (!string.IsNullOrWhiteSpace(groundedOutline))
        {
            try
            {
                string outlinePath = Path.Join(GetWorkspaceFolderPath(sessionId), "grounded-outline.txt");
                await File.WriteAllTextAsync(outlinePath, groundedOutline);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not save grounded-outline.txt: {Message}", SanitizeLogMessage(ex.Message));
            }
        }

        // Facts about the included set, so the abstract cannot call preprints "peer-reviewed studies".
        var venueBreakdown = finalState.SynthesizedRecords
            .GroupBy(r => r.VenueType)
            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")
            .ToList();
        string includedFacts = $"{finalState.SynthesizedRecords.Count} included record(s)" +
            (venueBreakdown.Count > 0 ? $" ({string.Join(", ", venueBreakdown)})" : "") +
            $"; the peer-reviewed-only filter was {(finalState.PeerReviewOnlyToggle ? "ON" : "OFF")}.";

        string screeningFacts = finalState.Stats.HumanReviewed > 0
            ? $"a language model screened the records and a human reviewer then checked {finalState.Stats.HumanReviewed} decisions and changed {finalState.Stats.HumanOverrides}. Do not claim any other human involvement."
            : "done by a language model only; do not claim that any human screened, verified or reviewed records during the run.";

        var reportPrompt = $$"""
            You are an elite academic meta-analyst preparing an evaluation report mapped to the PRISMA 2020 Expanded Checklist.

            CRITICAL LIVE RUN CONSTRAINTS (ZERO HALLUCINATION DIRECTIVE):
            - The primary search query for this run was exactly: "{{query}}"
            - To broaden recall (multi-perspective search), the engine additionally queried every source with these supplementary phrasings: {{supplementaryPerspectives}}
            - The review objective specified by the user was exactly: "{{explicitObjective}}"
            - The screening inclusion thresholds were exactly: "{{inc}}"
            - The screening exclusion thresholds were exactly: "{{exc}}"
            - Included set: {{includedFacts}} Do not describe the included studies as peer-reviewed unless the filter was ON, and do not state any count that is not given here.
            - Screening: {{screeningFacts}}

            {{PromptSafety.ReviewerInputNotice}}

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            {{PromptSafety.DataOnlyNotice}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{PromptSafety.Wrap(groundedChunksText, "excerpts from the included papers")}}

            PRE-COMPUTED GROUNDED OUTLINE (claims already checked against the sources above - ground synthesisResultsItem and discussionItem on these claims):
            {{(string.IsNullOrWhiteSpace(groundedOutline) ? "(No outline could be generated - ground your synthesis directly on the raw context chunks above instead.)" : groundedOutline)}}

            CITATION GROUNDING REQUIREMENT (mandatory traceability):
            When writing "synthesisResultsItem" and "discussionItem", every specific claim, finding, or comparison drawn from the literature MUST end with an inline citation marker in square brackets referencing the OFFICIAL ALPHABETIZED REFERENCE LIST above by its number, e.g. "...improves fault isolation [3]." or "...as shown in both [2] and [5]." Only cite numbers that actually appear in that list - never invent one. General framing sentences that state no specific finding do not need a citation marker.

            TASK:
            Generate a fully formed, detailed academic paragraph for each checklist field below.

            Respond ONLY with a single valid minified JSON object matching the template below. Plain text in every field: no Markdown, no diagram code.

            JSON TEMPLATE SCHEMA:
            {
                "titleItem": "A single-line systematic review title (at most 25 words, no full stop) on '{{query}}' mapping to PRISMA Item 1.",
                "abstractItem": "A formal abstract overview addressing the core objective ('{{explicitObjective}}') mapping to PRISMA Item 2.",
                "rationaleItem": "A rigorous context justification on the current state of research on '{{query}}' and why a review is needed, mapping to PRISMA Item 3.",
                "objectivesItem": "The explicit question formulation matching the stated goal: '{{explicitObjective}}' mapping to PRISMA Item 4.",
                "biasAssessmentItem": "This field will be post-processed. Output exactly: 'PREDEFINED_METADATA_MARKER'",
                "synthesisResultsItem": "Your simple-English factual literature summary paragraph mapping to PRISMA Item 20a. Do not place code syntax here.",
                "discussionItem": "Provide a general architectural interpretation with multi-source citations mapping to PRISMA Item 23a.",
                "supportItem": "This field will be post-processed. Output exactly: 'PREDEFINED_SUPPORT_MARKER'",
                "availabilityItem": "This field will be post-processed. Output exactly: 'PREDEFINED_REPO_MARKER'"
            }
            """;

        try
        {
            // FORCE JSON MODE: Configure Mistral to guarantee mathematically valid JSON token structures
            var structuralSettings = new MistralAIPromptExecutionSettings
            {
                Temperature = 0.2
            };

            // Universally force JSON mode via the underlying extension dictionary properties
            structuralSettings.ExtensionData ??= new Dictionary<string, object>();
            structuralSettings.ExtensionData["response_format"] = new { type = "json_object" };

            ChatMessageContent response;
            using (LlmStage.Begin("report-draft"))
                response = await chat.GetChatMessageContentAsync(reportPrompt, structuralSettings);
            string rawResponse = response.ToString().Trim();

            string mermaidBlock = "";
            var mermaidMatch = Regex.Match(rawResponse, @"\[MERMAID_START\](.*?)\[MERMAID_END\]", RegexOptions.Singleline);
            if (mermaidMatch.Success)
            {
                // Labels like "Recovery (+20%) [4]" break Mermaid unless quoted; repair before storing.
                string cleanMermaid = MermaidSanitizer.Sanitize(mermaidMatch.Groups[1].Value);
                if (cleanMermaid.Length > 0) mermaidBlock = "\n\n[MERMAID_START]\n" + cleanMermaid + "\n[MERMAID_END]\n";
            }

            string tikzBlock = "";
            var tikzMatch = Regex.Match(rawResponse, @"\[TIKZ_START\](.*?)\[TIKZ_END\]", RegexOptions.Singleline);
            if (tikzMatch.Success)
            {
                tikzBlock = "\n\n[TIKZ_START]\n" + tikzMatch.Groups[1].Value.Trim() + "\n[TIKZ_END]\n";
            }

            string jsonSearchText = rawResponse;
            jsonSearchText = Regex.Replace(jsonSearchText, @"\[MERMAID_START\].*?\[MERMAID_END\]", "", RegexOptions.Singleline);
            jsonSearchText = Regex.Replace(jsonSearchText, @"\[TIKZ_START\].*?\[TIKZ_END\]", "", RegexOptions.Singleline);

            // The first complete object, with line breaks inside quotes kept as \n (see LlmJson.ExtractObject).
            string jsonPart = jsonSearchText.Contains('{') ? LlmJson.ExtractObject(jsonSearchText) : "{}";

            // CLEANING PASS: Clean hidden ASCII control characters and wrap unescaped path backslashes
            jsonPart = Regex.Replace(jsonPart, "[\x00-\x1F]", " "); 
            jsonPart = Regex.Replace(jsonPart, @"\\(?![""\\/bfnrtu])", @"\\");

            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            string ResolveField(string propertyName) =>
                root.TryGetProperty(propertyName, out var element) ? element.GetString() ?? "" : "Extraction failed.";

            // 1. EXTRACT RAW BASELINE DATA FIELDS
            string rawTitle = ResolveField("titleItem");
            string rawAbstract = ResolveField("abstractItem");
            string rawRationale = ResolveField("rationaleItem");
            string rawObjectives = ResolveField("objectivesItem");
            // PRISMA Items 5-8 describe what the run did, so they are rendered from the run's own configuration
            // and ledger rather than written by the model (see MethodsSectionWriter for why).
            var selectedNames = finalState.SelectedSources.Select(SourceCatalog.DisplayNameFor).ToList();
            var unavailableNames = finalState.UnavailableSources.Select(SourceCatalog.DisplayNameFor).ToList();
            string methodsEligibility = MethodsSectionWriter.Eligibility(inc, exc, finalState.PeerReviewOnlyToggle);
            string methodsSources = MethodsSectionWriter.InformationSources(selectedNames, unavailableNames, finalState.SearchLogs, finalState.Timestamp,
                finalState.Stats.IdentifiedViaCitations, finalState.CitationChainingRequested);
            string methodsSearch = MethodsSectionWriter.SearchStrategy(query, effectivePerspectives, finalState.MaxResultsPerSource,
                finalState.Stats.DuplicatesRemoved, finalState.Stats.CappedBeyondMaxResults, finalState.YearFrom, finalState.YearTo, finalState.Stats.OutsideDateRange,
                finalState.SearchStringsReviewed, finalState.SearchStringYields);
            string methodsSelection = MethodsSectionWriter.SelectionProcess(finalState.PeerReviewOnlyToggle, finalState.Stats, finalState.HumanScreeningReviewRequested, finalState.HumanScreeningReviewOutcome, Llm.DisplayName);
            if (ExclusionReasons.Sentence(finalState.Phases.Screening) is { } exclusionSentence)
                methodsSelection += " " + exclusionSentence;
            string rawDiscussion = ResolveField("discussionItem");
            string rawSynthesisText = ResolveField("synthesisResultsItem");

            var deltas = new List<StyleDeltaLog>();

            // 2. PASS THE PROSE FIELDS THROUGH THE STYLISTIC REFINER
            // Only free prose is copy-edited. The title is left alone (the refiner once turned it into a
            // five-sentence paragraph), and the methods items are generated from run data so there is nothing
            // to copy-edit - rewriting them is how a search string got silently shortened in the report.
            string cleanTitle = CleanTitle(rawTitle, query);
            deltas.Add(new StyleDeltaLog("Title", rawTitle, cleanTitle, Applied: false, Note: "Title is not sent through the stylistic pass."));
            string cleanAbstract, cleanRationale, cleanObjectives;
            using (LlmStage.Begin("style"))
            {
                var (a, dAbstract) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", rawAbstract); deltas.Add(dAbstract); cleanAbstract = a;
                var (r, dRationale) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Rationale", rawRationale); deltas.Add(dRationale); cleanRationale = r;
                var (o, dObjectives) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Objectives", rawObjectives); deltas.Add(dObjectives); cleanObjectives = o;
            }
            // The Results & Synthesis (20a) and Discussion (23a) sections are NOT run through the generic
            // stylistic refiner - that rewrite step tends to drop the inline [n] citation markers. They are
            // written by the thematic synthesis (one cited subsection per theme, then the discussion), or, when
            // that is switched off or fails, by a single citation-mandatory pass; both are then peer reviewed.
            var papersForCheck = referencedPapers ?? Array.Empty<ReferencedPaper>();
            ThematicResult? thematic = null;
            if (Synthesis.ThematicSynthesis && papersForCheck.Count > 0)
            {
                try
                {
                    thematic = await RunThematicSynthesisAsync(sessionId, chat, query, explicitObjective, finalState, papersForCheck, referenceCount);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Thematic synthesis failed, falling back to the single-pass synthesis: {Message}", SanitizeLogMessage(ex.Message));
                    thematic = new ThematicResult { Codebook = new ThematicCodebook { FallbackReason = $"the thematic synthesis failed ({SanitizeLogMessage(ex.Message)})" } };
                }
                finalState.ThematicSynthesis = thematic.Codebook;
            }
            bool useThematic = thematic != null && thematic.Sections.Count > 0 && !string.IsNullOrWhiteSpace(thematic.Discussion);
            if (thematic != null && !useThematic && thematic.Codebook.FallbackReason == null)
                thematic.Codebook.FallbackReason = "the discussion could not be written from the theme subsections";

            PeerReviewLog peerReview;
            string synthesisProse;
            string discussionText;
            var sections = new List<SynthesisSection>();
            if (useThematic)
            {
                sections = thematic!.Sections;
                synthesisProse = ThematicSynthesis.Overview(thematic.Codebook, referenceCount);
                discussionText = thematic.Discussion;
                peerReview = thematic.PeerReview ?? new PeerReviewLog { GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"), Verdict = "Peer review not run" };
            }
            else
            {
                string citedSynthesis, citedDiscussion;
                ReportProgress(sessionId, finalState, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 3, 0), "Writing the synthesis and discussion");
                using (LlmStage.Begin("cited-sections"))
                    (citedSynthesis, citedDiscussion) = await GenerateCitedSectionsAsync(
                        chat, query, explicitObjective, groundedOutline, referenceListMapping, groundedChunksText, referenceCount);

                // Fall back to the base multi-field draft only if the dedicated pass returned nothing.
                if (string.IsNullOrWhiteSpace(citedSynthesis)) citedSynthesis = rawSynthesisText;
                if (string.IsNullOrWhiteSpace(citedDiscussion)) citedDiscussion = rawDiscussion;

                // Documented "peer reviewer" pass: an LLM critiques the two sections and revises them.
                ReportProgress(sessionId, finalState, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 5, 0), "Automated peer review");
                using (LlmStage.Begin("automated-peer-review"))
                    peerReview = await PeerReviewAndReviseAsync(chat, citedSynthesis, citedDiscussion, referenceListMapping, groundedOutline);
                synthesisProse = peerReview.SynthesisAfter;
                discussionText = peerReview.DiscussionAfter;
            }
            try
            {
                string peerReviewPath = Path.Join(GetWorkspaceFolderPath(sessionId), "peer-review-feedback.json");
                await File.WriteAllTextAsync(peerReviewPath, JsonSerializer.Serialize(peerReview, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not save peer-review-feedback.json: {Message}", SanitizeLogMessage(ex.Message));
            }

            // The model sometimes writes Markdown (headings, lists, tables, **emphasis**); turn it back into plain
            // prose before the citation check, so the checked sentences are exactly the ones shown on the page
            // and in main.tex. Every change is listed under FormattingChanges in citation-audit.json.
            // Fields are kept in report order: the synthesis overview, the theme subsections, the discussion.
            // The artifact the reviewer asked for, built from the finished results; its cited elements go through
            // the same range check, support check and repair as the prose.
            ReviewArtifact? artifact = null;
            string artifactKind = ArtifactKinds.Normalize(finalState.ArtifactKind);
            if (artifactKind != ArtifactKinds.None)
            {
                ReportProgress(sessionId, finalState, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 6, 0), "Building the artifact");
                string results = sections.Count > 0
                    ? string.Join("\n\n", sections.Select(x => $"THEME: {x.Heading}\n{x.Text}")) + $"\n\nDISCUSSION:\n{discussionText}"
                    : $"{synthesisProse}\n\nDISCUSSION:\n{discussionText}";
                using (LlmStage.Begin("artifact"))
                    artifact = await ArtifactBuilder.BuildAsync(chat, artifactKind, finalState.SynthesisTargetDirective ?? "",
                        explicitObjective, results, referenceListMapping, referenceCount);
            }

            var formattingChanges = new List<FormattingChange>();
            string Plain(string text, string field)
            {
                var (clean, changes) = ProseCleaner.Clean(text, field);
                formattingChanges.AddRange(changes);
                return clean;
            }
            var fields = new List<(string Field, string Text)> { ("synthesisResultsItem", Plain(synthesisProse, "synthesisResultsItem")) };
            fields.AddRange(sections.Select(s => (s.Field, Plain(s.Text, s.Field))));
            fields.Add(("discussionItem", Plain(discussionText, "discussionItem")));
            if (artifact is { Error: null }) fields.AddRange(artifact.CitableTexts().ToList());

            // STORM-style traceability guardrail: every [n] citation marker the model writes must resolve to
            // an entry that actually exists in the run's reference list. The model occasionally invents or
            // miscounts numbers (e.g. citing [56] against a 53-entry list); strip those instead of shipping
            // a report whose citations don't trace back to a real source, and log every removal to an
            // on-disk audit artifact so the discrepancy itself stays visible rather than silently vanishing.
            // The stripping logic lives in CitationValidator so it can be unit-tested in isolation.
            var citationStrips = new List<StrippedCitation>();
            for (int i = 0; i < fields.Count; i++)
            {
                var validation = CitationValidator.ValidateAndStrip(fields[i].Text, referenceCount, fields[i].Field);
                fields[i] = (fields[i].Field, validation.CleanedText);
                citationStrips.AddRange(validation.Stripped);
            }
            finalState.Stats.InvalidCitationsStripped += citationStrips.Count;

            // Citation SUPPORT check: does the cited paper actually say what the sentence claims? The range
            // check above cannot catch a valid-looking [n] that points at the wrong paper. The verified
            // extraction quotes are given to the check as evidence too: they are what the synthesis was written from.
            var verifiedFindings = (finalState.Extractions ?? new List<StudyExtraction>())
                .ToDictionary(e => e.ReferenceNumber, e => (IReadOnlyList<string>)ThematicSynthesis.VerifiedFindings(e).Select(f => f.Finding.Quote).ToList());
            var citedSentences = fields.SelectMany(f => CitationSupportChecker.ExtractCitedSentences(f.Text, f.Field)).ToList();
            List<CitationSupportResult> supportChecks;
            ReportProgress(sessionId, finalState, RunProgress.Checking, 0, $"Checking {citedSentences.Sum(c => c.References.Count)} citations");
            using (LlmStage.Begin("citation-check"))
                supportChecks = await CitationSupportChecker.CheckAsync(chat, citedSentences, papersForCheck,
                    verifiedFindings: verifiedFindings, parallelism: Llm.ScreeningParallelism, secondCheck: Synthesis.SecondCitationCheck,
                    progress: (second, done, total) => ReportProgress(sessionId, finalState, RunProgress.Checking,
                        RunProgress.Phase(RunProgress.CheckingPhases, second ? 1 : 0, (double)done / total),
                        second ? $"Second check: {Of(done, total, "papers")}" : $"First check: {Of(done, total, "papers")}"));
            var initialSummary = CitationSupportSummary.From(supportChecks);

            // Repair: sentences whose citation was rejected are rewritten once from the cited paper's evidence
            // (or lose that citation) and checked again. Before and after go into citation-audit.json.
            var repairs = new List<CitationRepair>();
            if (Synthesis.RepairCitations && supportChecks.Any(NeedsRepair))
            {
                ReportProgress(sessionId, finalState, RunProgress.Checking, RunProgress.Phase(RunProgress.CheckingPhases, 2, 0),
                    $"Repairing {supportChecks.Count(NeedsRepair)} citations and checking them again");
                using (LlmStage.Begin("citation-repair"))
                    (fields, supportChecks, repairs) = await RepairCitationsAsync(chat, fields, supportChecks, papersForCheck, verifiedFindings, referenceCount);
            }
            finalState.Stats.CitationsRepaired = repairs.Count(r => r.Action is "rewrite" or "drop_citation" or "delete");

            // Theme consistency: a subsection citing a study that was not coded under its theme is flagged (not removed).
            var themeOfField = sections.ToDictionary(s => s.Field, s => thematic?.Codebook.Themes.FirstOrDefault(t => t.Id == s.ThemeId));
            foreach (var check in supportChecks)
            {
                if (themeOfField.TryGetValue(check.Field, out var theme) && theme != null && !theme.Studies.Contains(check.Reference))
                    check.ThemeNote = $"Study [{check.Reference}] was not coded under the theme \"{theme.Name}\".";
            }
            finalState.Stats.CitationsOutsideTheme = supportChecks.Count(c => c.ThemeNote != null);

            var supportSummary = CitationSupportSummary.From(supportChecks);
            finalState.Stats.CitationsChecked = supportSummary.Checked;
            finalState.Stats.CitationsSupported = supportSummary.Supported;
            finalState.Stats.CitationsPartiallySupported = supportSummary.PartiallySupported;
            finalState.Stats.CitationsNotSupported = supportSummary.NotSupported;
            finalState.Stats.CitationsUnverifiable = supportSummary.Unverifiable;
            finalState.Stats.CitationsInsufficientEvidence = supportSummary.InsufficientEvidence;
            finalState.Stats.CitationsSecondChecked = supportSummary.SecondChecked;
            finalState.Stats.CitationsUpgradedBySecondCheck = supportSummary.UpgradedBySecondCheck;

            // Coverage: which included studies the synthesis and discussion cite, and why the others are not cited.
            var textOf = fields.ToDictionary(f => f.Field, f => f.Text);
            var citedStudies = new SortedSet<int>(fields.SelectMany(f => ThematicSynthesis.CitedIn(f.Text)).Where(r => r >= 1 && r <= referenceCount));
            var notCited = new List<UncodedStudy>();
            for (int refNo = 1; refNo <= referenceCount; refNo++)
            {
                if (citedStudies.Contains(refNo)) continue;
                var uncodedReason = thematic?.Codebook.Uncoded.FirstOrDefault(u => u.Reference == refNo)?.Reason;
                var theme = useThematic ? thematic!.Codebook.Themes.FirstOrDefault(t => t.Studies.Contains(refNo)) : null;
                notCited.Add(new UncodedStudy(refNo,
                    uncodedReason != null ? $"not coded: {uncodedReason.TrimEnd('.')}" :
                    theme != null ? $"coded under \"{theme.Name}\" but not cited after the fill pass, peer review and citation repair" :
                    "not cited by the synthesis"));
            }
            if (thematic != null)
            {
                thematic.Codebook.NotCited = notCited;
                foreach (var cov in thematic.Codebook.Coverage)
                {
                    if (!textOf.TryGetValue(cov.Field, out var finalText)) continue;
                    cov.CitedFinal = ThematicSynthesis.CitedIn(finalText).Intersect(cov.Studies).ToList();
                    cov.NotCited = cov.Studies.Except(cov.CitedFinal).ToList();
                }
                finalState.Stats.ThemesIdentified = useThematic ? thematic.Codebook.Themes.Count : 0;
                finalState.Stats.StudiesCoded = useThematic ? thematic.Codebook.CodedStudies.Count() : 0;
                finalState.Stats.CodingKappa = useThematic ? thematic.Codebook.SecondCoding?.Kappa : null;
                try
                {
                    await File.WriteAllTextAsync(Path.Join(GetWorkspaceFolderPath(sessionId), "thematic-codebook.json"),
                        JsonSerializer.Serialize(thematic.Codebook, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not save thematic-codebook.json: {Message}", SanitizeLogMessage(ex.Message));
                }
            }
            finalState.Stats.StudiesCitedInSynthesis = citedStudies.Count;
            string coverageSentence = ThematicSynthesis.CoverageSentence(citedStudies.Count, referenceCount, notCited);

            // Re-append the isolated diagram blocks to the overview field.
            string fullSynthesisField = textOf["synthesisResultsItem"] + mermaidBlock + tikzBlock;
            string cleanDiscussionValidated = textOf["discussionItem"];
            foreach (var s in sections) s.Text = textOf[s.Field];
            if (artifact is { Error: null })
            {
                foreach (var (field, _) in artifact.CitableTexts().ToList()) artifact.SetText(field, textOf[field]);
                artifact.Compact();
                artifact.Mermaid = ArtifactBuilder.ToMermaid(artifact);
            }

            try
            {
                string auditPath = Path.Join(GetWorkspaceFolderPath(sessionId), "citation-audit.json");
                var auditPayload = new
                {
                    ReferenceListSize = referenceCount,
                    InvalidMarkersStripped = citationStrips.Count,
                    Details = citationStrips,
                    InitialSupportSummary = initialSummary,
                    SupportSummary = supportSummary,
                    Repairs = repairs,
                    StudiesCited = citedStudies.Count,
                    FormattingChanges = formattingChanges,
                    NotCited = notCited,
                    SupportChecks = supportChecks
                };
                await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(auditPayload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not save citation-audit.json: {Message}", SanitizeLogMessage(ex.Message));
            }

            string checkSentence = supportSummary.ToSentence();
            if (repairs.Count > 0)
                checkSentence += $" Before this final count, {initialSummary.NotSupported} citation{(initialSummary.NotSupported == 1 ? " was" : "s were")} judged not supported and {initialSummary.PartiallySupported} partially supported; {finalState.Stats.CitationsRepaired} cited sentence{(finalState.Stats.CitationsRepaired == 1 ? " was" : "s were")} rewritten or had the citation removed once and checked again (listed under Repairs in citation-audit.json).";
            if (finalState.Stats.CitationsOutsideTheme > 0)
                checkSentence += $" {finalState.Stats.CitationsOutsideTheme} citation{(finalState.Stats.CitationsOutsideTheme == 1 ? "" : "s")} in a theme subsection refer to a study not coded under that theme and are flagged in the audit.";

            var reportObj = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = cleanTitle,
                AbstractItem = Plain(!string.IsNullOrWhiteSpace(cleanAbstract) ? cleanAbstract : rawAbstract, "abstractItem"),
                RationaleItem = Plain(!string.IsNullOrWhiteSpace(cleanRationale) ? cleanRationale : rawRationale, "rationaleItem"),
                ObjectivesItem = !string.IsNullOrWhiteSpace(cleanObjectives) ? cleanObjectives : rawObjectives,
                EligibilityItem = methodsEligibility,
                SourcesItem = methodsSources,
                SearchStrategyItem = methodsSearch,
                SelectionProcessItem = methodsSelection,
                SynthesisResultsItem = fullSynthesisField,
                SynthesisSections = sections,
                SynthesisMethodsItem = ThematicSynthesis.MethodsText(useThematic ? thematic!.Codebook : thematic?.Codebook is { } fb ? new ThematicCodebook { FallbackReason = fb.FallbackReason } : null, Llm.DisplayName, Synthesis.RepairCitations)
                    + (artifact == null ? "" : artifact.Error == null
                        ? $" The {ArtifactKinds.All.First(k => k.Key == artifact.Requested).Label.ToLowerInvariant()} requested by the reviewer was built by the model from the finished results only, and the elements in it that cite a study were checked like the sentences of the report."
                        : $" The requested artifact could not be built: {artifact.Error}"),
                Artifact = artifact,
                CoverageSummary = coverageSentence,
                DiscussionItem = cleanDiscussionValidated,
                // Items 9-11 from the extraction and MMAT appraisal the run actually produced.
                BiasAssessmentItem = MethodsSectionWriter.DataAndAppraisal(finalState.Extractions ?? new List<StudyExtraction>(), Llm.DisplayName),
                ProtocolItem = MethodsSectionWriter.Protocol(finalState.ProtocolSha256, finalState.Timestamp),
                SupportItem = _supportStatement,
                ProtocolHash = finalState.ProtocolHash,
                CitationCheckSummary = checkSentence,
                AvailabilityItem = $"The code of the tool (version {RecordingChatCompletionService.AppVersion}) is openly available at https://github.com/lauPhilip/au-btech-literature-review-agent. The run archive contains the protocol, the raw source responses, the screening ledger, the extraction and appraisal data, the thematic codebook, every model call's fingerprint and a manifest.json with the SHA-256 fingerprint of every file. Journal quartiles are taken from the SCImago Journal Rank (SJR) data (https://www.scimagojr.com)."
            };

            // 3. ARCHIVE THE AUDIT DELTA LEDGER DIRECTLY INTO THE ACTIVE WORKSPACE STORE SUBFOLDER
            string ledgerPath = Path.Join(GetWorkspaceFolderPath(sessionId), "stylistic-transformation-ledger.json");
            await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(deltas, new JsonSerializerOptions { WriteIndented = true }));

            // 4. WRITE THE MAIN CONSOLIDATED REPORT
            await File.WriteAllTextAsync(GetReportFilePath(sessionId), JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception fatalEx)
        {
            _log.LogError(fatalEx, "Run {RunId}: report generation failed", sessionId);
            var failureReport = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = $"Checklist Synthesis generation completely faulted: {SanitizeLogMessage(fatalEx.Message)}"
            };
            await File.WriteAllTextAsync(GetReportFilePath(sessionId), JsonSerializer.Serialize(failureReport, new JsonSerializerOptions { WriteIndented = true }));
            finalState.FailureMessage = $"The report could not be generated: {SanitizeLogMessage(fatalEx.Message)}";
        }
    }

    /// <summary>
    /// Keeps the title a title: first line only, no trailing full stop, and if the model returned a
    /// paragraph (or nothing) fall back to a neutral title built from the query.
    /// </summary>
    public static string CleanTitle(string rawTitle, string query)
    {
        string t = (rawTitle ?? "").Split('\n')[0].Trim().TrimEnd('.');
        int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool looksLikeParagraph = Regex.Matches(t, @"[.!?]\s+[A-Z]").Count > 0 || words > 30;
        if (string.IsNullOrWhiteSpace(t) || t == "Extraction failed" || looksLikeParagraph)
            return $"{query.Trim()}: A Systematic Literature Review";
        return t;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

/// <summary>The systematic report, step by step: the outline, the first PRISMA draft, then the steps that write and check the report.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// What the steps of one systematic report share: the run, its model and reference list, and what each step has
    /// written so far. The steps run in the order GeneratePrismaChecklistReportWithRAGAsync calls them.
    /// </summary>
    private sealed class ReportRun
    {
        public Guid SessionId { get; init; }
        public IChatCompletionService Chat { get; init; } = null!;
        public string Query { get; init; } = "";
        public string Objective { get; init; } = "";
        public ReviewState State { get; init; } = null!;
        public string ReferenceList { get; init; } = "";
        public int ReferenceCount { get; init; }
        public string GroundedChunks { get; init; } = "";
        public IReadOnlyList<ReferencedPaper> Papers { get; init; } = Array.Empty<ReferencedPaper>();
        public string IncludedFacts { get; init; } = "";
        public string ScreeningFacts { get; init; } = "";

        /// <summary>
        /// The grounded outline and the first PRISMA draft, written the first time a step needs them: the fallback
        /// writer, or the front matter when its own step failed. A run whose thematic synthesis and front matter succeed
        /// never writes them (about 6 % of a run's tokens went to them for nothing).
        /// </summary>
        public Lazy<Task<FallbackDraft>> Fallback { get; init; } = null!;

        /// <summary>The first draft when it was written and succeeded, otherwise null.</summary>
        public ReportDraft? DraftIfWritten => Fallback.IsValueCreated && Fallback.Value.IsCompletedSuccessfully ? Fallback.Value.Result.Draft : null;
        public SystematicMethods Methods { get; init; } = null!;
        public ThematicResult? Thematic { get; init; }
        public bool UseThematic { get; init; }

        public List<SynthesisSection> Sections { get; set; } = new();
        public string SynthesisProse { get; set; } = "";
        public string DiscussionText { get; set; } = "";
        public ReviewArtifact? Artifact { get; set; }
        public ClosingSections? Closing { get; set; }
        public string Title { get; set; } = "";
        public string Abstract { get; set; } = "";
        public string Rationale { get; set; } = "";
        public string Objectives { get; set; } = "";
        public List<StyleDeltaLog> Deltas { get; } = new();
        public List<FormattingChange> FormattingChanges { get; } = new();

        /// <summary>The text as plain prose (ProseCleaner), with every change kept for citation-audit.json.</summary>
        public string Plain(string text, string field)
        {
            var (clean, changes) = ProseCleaner.Clean(text, field);
            FormattingChanges.AddRange(changes);
            return clean;
        }
    }

    /// <summary>The grounded outline and the first PRISMA draft written from it.</summary>
    private sealed record FallbackDraft(string Outline, ReportDraft Draft);

    /// <summary>The fields of the first PRISMA draft, and the diagram blocks taken out of it.</summary>
    private sealed record ReportDraft(string MermaidBlock, string TikzBlock, string Title, string Abstract, string Rationale,
        string Objectives, string Discussion, string SynthesisText);

    private async Task GeneratePrismaChecklistReportWithRAGAsync(Guid sessionId, IChatCompletionService chat, string query, string explicitObjective, string inc, string exc, string groundedChunksText, string referenceListMapping, ReviewState finalState, List<string>? searchPerspectives = null, int referenceCount = 0, IReadOnlyList<ReferencedPaper>? referencedPapers = null)
    {
        var effectivePerspectives = searchPerspectives ?? new List<string> { query };
        string supplementaryPerspectives = effectivePerspectives.Count > 1
            ? string.Join(" | ", effectivePerspectives.Skip(1))
            : "(none - single literal query only)";

        ReportProgress(sessionId, finalState, RunProgress.Synthesis, 0, "Outline and report draft");
        // The thematic synthesis reads neither the outline nor the first draft (the draft is only its fallback), so it
        // starts now; it is awaited where its result is used, and on every way out.
        var papersForCheck = referencedPapers ?? Array.Empty<ReferencedPaper>();
        Task<ThematicResult>? thematicTask = Synthesis.ThematicSynthesis && papersForCheck.Count > 0
            ? ThematicOrFallbackAsync(sessionId, chat, query, explicitObjective, finalState, papersForCheck, referenceCount)
            : null;

        string includedFacts = IncludedFacts(finalState);
        string screeningFacts = ScreeningFacts(finalState);
        var fallback = new Lazy<Task<FallbackDraft>>(async () =>
        {
            string groundedOutline = await WriteGroundedOutlineAsync(sessionId, chat, query, explicitObjective, groundedChunksText, referenceListMapping);
            string reportPrompt = ReportDraftPrompt(query, supplementaryPerspectives, explicitObjective, inc, exc, includedFacts, screeningFacts,
                referenceListMapping, groundedChunksText, groundedOutline);
            return new FallbackDraft(groundedOutline, await WriteReportDraftAsync(chat, reportPrompt));
        });

        try
        {
            var methods = MethodsFromLedger(finalState, query, inc, exc, effectivePerspectives);

            // Only free prose is copy-edited. The title is left alone (the refiner once turned it into a
            // five-sentence paragraph), and the methods items are generated from run data so there is nothing
            // to copy-edit - rewriting them is how a search string got silently shortened in the report.
            // The Results & Synthesis (20a) and Discussion (23a) sections are NOT run through the generic
            // stylistic refiner - that rewrite step tends to drop the inline [n] citation markers. They are
            // written by the thematic synthesis (one cited subsection per theme, then the discussion), or, when
            // that is switched off or fails, by a single citation-mandatory pass; both are then peer reviewed.
            ThematicResult? thematic = null;
            if (thematicTask != null)
            {
                thematic = await thematicTask;
                finalState.ThematicSynthesis = thematic.Codebook;
            }
            bool useThematic = thematic != null && thematic.Sections.Count > 0 && !string.IsNullOrWhiteSpace(thematic.Discussion);
            if (thematic != null && !useThematic && thematic.Codebook.FallbackReason == null)
                thematic.Codebook.FallbackReason = "the discussion could not be written from the theme subsections";

            var run = new ReportRun
            {
                SessionId = sessionId, Chat = chat, Query = query, Objective = explicitObjective, State = finalState,
                ReferenceList = referenceListMapping, ReferenceCount = referenceCount, GroundedChunks = groundedChunksText,
                Papers = papersForCheck, IncludedFacts = includedFacts, ScreeningFacts = screeningFacts,
                Fallback = fallback, Methods = methods, Thematic = thematic, UseThematic = useThematic,
            };
            await WriteSynthesisAndDiscussionAsync(run);
            await WriteFrontArtifactAndClosingAsync(run);
            var check = await CheckCitationsAsync(run);
            var coverage = await RecordCoverageAsync(run, check.Fields);
            await WriteReportFilesAsync(run, check, coverage);
        }
        catch (Exception fatalEx)
        {
            // The thematic synthesis may still be running beside a step that failed; it must not outlive the report.
            if (thematicTask != null) await thematicTask;
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
    /// STORM-style outline stage: before writing prose, the model maps out which specific, grounded claims the
    /// sources actually support and which reference numbers back each one. It is saved as grounded-outline.txt and
    /// fed to the draft and the synthesis, so they are written against a pre-checked claim map instead of free-associating.
    /// </summary>
    private async Task<string> WriteGroundedOutlineAsync(Guid sessionId, IChatCompletionService chat, string query, string explicitObjective,
        string groundedChunksText, string referenceListMapping)
    {
        string groundedOutline;
        using (LlmStage.Begin("outline"))
            groundedOutline = await ReviewWriter.WriteOutlineAsync(chat, WritingProfile.Systematic, query, explicitObjective, groundedChunksText, referenceListMapping,
                message => _log.LogWarning("{Message}", SanitizeLogMessage(message)));
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
        return groundedOutline;
    }

    private static string ReportDraftPrompt(string query, string supplementaryPerspectives, string explicitObjective, string inc, string exc,
        string includedFacts, string screeningFacts, string referenceListMapping, string groundedChunksText, string groundedOutline)
    {
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
        return reportPrompt;
    }

    /// <summary>
    /// The first PRISMA draft: one JSON object with every free-text item. Its title, abstract, rationale and
    /// objectives are used only when the front matter step fails, and its synthesis and discussion only when the
    /// cited pass returns nothing.
    /// </summary>
    private static async Task<ReportDraft> WriteReportDraftAsync(IChatCompletionService chat, string reportPrompt)
    {
        // JSON mode, so the answer is one JSON object.
        var structuralSettings = new MistralAIPromptExecutionSettings
        {
            Temperature = 0.2
        };
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

        string rawTitle = ResolveField("titleItem");
        string rawAbstract = ResolveField("abstractItem");
        string rawRationale = ResolveField("rationaleItem");
        string rawObjectives = ResolveField("objectivesItem");
        string rawDiscussion = ResolveField("discussionItem");
        string rawSynthesisText = ResolveField("synthesisResultsItem");
        return new ReportDraft(mermaidBlock, tikzBlock, rawTitle, rawAbstract, rawRationale, rawObjectives, rawDiscussion, rawSynthesisText);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The synthesis and discussion of a systematic report: from the thematic synthesis, or from one cited pass and its peer review.</summary>
public partial class PrismaReviewEngine
{
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
            4. {{ReviewWriter.CitationLoadRule(WritingProfile.Systematic)}}

            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "synthesis": "the full Results and Synthesis text with inline [n] citations", "discussion": "the full Discussion text with inline [n] citations" }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<SectionsAnswer>(chat, prompt, JsonMode(0.3), a =>
                string.IsNullOrWhiteSpace(a.Synthesis) || string.IsNullOrWhiteSpace(a.Discussion) ? "both synthesis and discussion must be non-empty text." :
                ReviewWriter.CitationLoadProblem(a.Synthesis, part: "the synthesis") ?? ReviewWriter.CitationLoadProblem(a.Discussion, part: "the discussion"), repairAttempts: 2);
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
            do not invent numbers or fabricate claims. {{ReviewWriter.CitationLoadRule(WritingProfile.Systematic)}}

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
                string.IsNullOrWhiteSpace(a.Synthesis) && string.IsNullOrWhiteSpace(a.Discussion) ? "synthesis and discussion must not both be empty." :
                ReviewWriter.CitationLoadProblem(a.Synthesis, synthesis, "the synthesis") ?? ReviewWriter.CitationLoadProblem(a.Discussion, discussion, "the discussion"));
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

    /// <summary>
    /// The synthesis and discussion: from the thematic synthesis when it wrote them, otherwise from one
    /// citation-mandatory pass and its peer review. The peer review is saved to peer-review-feedback.json either way.
    /// </summary>
    private async Task WriteSynthesisAndDiscussionAsync(ReportRun run)
    {
        PeerReviewLog peerReview;
        if (run.UseThematic)
        {
            run.Sections = run.Thematic!.Sections;
            run.SynthesisProse = ThematicSynthesis.Overview(run.Thematic!.Codebook, run.ReferenceCount);
            run.DiscussionText = run.Thematic!.Discussion;
            peerReview = run.Thematic!.PeerReview ?? new PeerReviewLog { GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"), Verdict = "Peer review not run" };
        }
        else
        {
            string citedSynthesis, citedDiscussion;
            ReportProgress(run.SessionId, run.State, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 3, 0), "Writing the synthesis and discussion");
            using (LlmStage.Begin("cited-sections"))
                (citedSynthesis, citedDiscussion) = await GenerateCitedSectionsAsync(
                    run.Chat, run.Query, run.Objective, run.Outline, run.ReferenceList, run.GroundedChunks, run.ReferenceCount);

            // Fall back to the base multi-field draft only if the dedicated pass returned nothing.
            if (string.IsNullOrWhiteSpace(citedSynthesis)) citedSynthesis = run.Draft.SynthesisText;
            if (string.IsNullOrWhiteSpace(citedDiscussion)) citedDiscussion = run.Draft.Discussion;

            ReportProgress(run.SessionId, run.State, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 5, 0), "Automated peer review");
            using (LlmStage.Begin("automated-peer-review"))
                peerReview = await PeerReviewAndReviseAsync(run.Chat, citedSynthesis, citedDiscussion, run.ReferenceList, run.Outline);
            run.SynthesisProse = peerReview.SynthesisAfter;
            run.DiscussionText = peerReview.DiscussionAfter;
        }
        try
        {
            string peerReviewPath = Path.Join(GetWorkspaceFolderPath(run.SessionId), "peer-review-feedback.json");
            await File.WriteAllTextAsync(peerReviewPath, JsonSerializer.Serialize(peerReview, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save peer-review-feedback.json: {Message}", SanitizeLogMessage(ex.Message));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>
    /// Part of the screening cache key: change it whenever a screening prompt changes, so decisions made
    /// with an older prompt are never reused. It sits beside the prompt it versions, so both change in one commit.
    /// </summary>
    public const string ScreeningPromptVersion = "screening-v7";

    /// <summary>
    /// The screening prompt for one record, used by the review run and by the screening evaluation tool so
    /// both measure exactly the same thing. Temperature 0 so repeated runs give the same decisions. The
    /// second screener works through the criteria in a different order (exclusion first, then positive
    /// evidence for inclusion), so the two screenings are not simply the same prompt twice.
    /// </summary>
    public static async Task<ScreeningAnswer> ScreenPaperAsync(IChatCompletionService chatService, AcademicPaper paper, string inclusionCriteria, string exclusionCriteria, bool secondScreener = false)
    {
        bool noAbstract = string.IsNullOrWhiteSpace(paper.Abstract);
        string prompt = ScreeningPrompt(paper, inclusionCriteria, exclusionCriteria, secondScreener);
        string? Parts(ScreeningAnswer a) => CriterionParts.Problem(a, inclusionCriteria);

        if (!noAbstract)
            return CriterionParts.Apply((await LlmJson.GetAsync<ScreeningAnswer>(chatService, prompt, JsonMode(0.0), a => ScreeningAnswer.Validate(a) ?? Parts(a))).Normalized(), paper, inclusionCriteria);

        // A record without an abstract: an answer that still describes one is sent back. If the model keeps doing
        // it, the decision is kept but its reason is flagged, so a reader knows not to trust that part (issue #132).
        try
        {
            return CriterionParts.Apply((await LlmJson.GetAsync<ScreeningAnswer>(chatService, prompt, JsonMode(0.0), a => ScreeningAnswer.ValidateWithoutAbstract(a) ?? Parts(a))).Normalized(), paper, inclusionCriteria);
        }
        catch (LlmOutputException)
        {
            var answer = CriterionParts.Apply((await LlmJson.GetAsync<ScreeningAnswer>(chatService, prompt, JsonMode(0.0), a => ScreeningAnswer.Validate(a) ?? Parts(a))).Normalized(), paper, inclusionCriteria);
            if (ScreeningAnswer.ClaimsAnAbstract(answer.Reasoning) || ScreeningAnswer.ClaimsAnAbstract(answer.BriefSummary))
                answer.Reasoning = AbstractClaimWarning + " " + answer.Reasoning;
            return answer;
        }
    }

    /// <summary>The screening prompt for one record, as sent to the model.</summary>
    public static string ScreeningPrompt(AcademicPaper paper, string inclusionCriteria, string exclusionCriteria, bool secondScreener)
    {
        string authorList = string.Join(", ", paper.Authors ?? new List<string>());
        bool noAbstract = string.IsNullOrWhiteSpace(paper.Abstract);
        string record = $"""
            - Title: {paper.Title}
            - Authors: {authorList}
            - Date Context: {paper.PublishedDate}
            - Publication/Journal Venue: {paper.JournalSource}
            - Abstract: {(noAbstract ? "(none: this record has no abstract)" : paper.Abstract)}
            """;
        string noAbstractNotice = noAbstract
            ? "\nTHIS RECORD HAS NO ABSTRACT. Judge it on the title and venue only, and do not describe or quote an abstract in the reasoning or the summary.\n"
            : "";

        string procedure = ScreeningProcedure(secondScreener);

        return $$"""
            Evaluate the following academic paper against the provided systematically structured PRISMA parameters.

            CRITERIA DIRECTIVES (from the reviewer):
            - Inclusion Thresholds: {{inclusionCriteria}}
            - Exclusion Thresholds: {{exclusionCriteria}}

            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            PAPER TARGET DATA:
            {{PromptSafety.Wrap(record, "paper metadata and abstract")}}
            {{noAbstractNotice}}
            {{procedure}}
            {{CriterionParts.Procedure}}
            (The reference string is built separately from the source metadata - do not write one.)

            {{ScreeningAnswerFormat}}
            {{CriterionParts.AnswerField}}
            """;
    }

    private static readonly Lazy<string>[] ScreeningTemplates = { new(() => TemplateOf(false)), new(() => TemplateOf(true)) };

    /// <summary>
    /// The fingerprint of one screener's fixed prompt text, with and without an abstract, part of the screening cache key
    /// so an edit to the prompt, or to the shared <see cref="ScreeningProcedure"/>, is never answered from the cache.
    /// </summary>
    public static string ScreeningTemplateSha256(bool secondScreener) => ScreeningTemplates[secondScreener ? 1 : 0].Value;

    /// <summary>The screening prompts written with placeholders, with an abstract and without: the fixed text only.</summary>
    public static IEnumerable<string> ScreeningTemplate(bool secondScreener)
    {
        AcademicPaper Paper(string abstractText) => new(PromptTemplate.Slot("id"), PromptTemplate.Slot("title"), abstractText,
            PromptTemplate.Slot("date"), new List<string> { PromptTemplate.Slot("authors") }, PromptTemplate.Slot("venue"));
        string inclusion = PromptTemplate.Slot("inclusion"), exclusion = PromptTemplate.Slot("exclusion");
        yield return ScreeningPrompt(Paper(PromptTemplate.Slot("abstract")), inclusion, exclusion, secondScreener);
        yield return ScreeningPrompt(Paper(""), inclusion, exclusion, secondScreener);
    }

    private static string TemplateOf(bool secondScreener) => PromptTemplate.Hash(ScreeningTemplate(secondScreener));

    /// <summary>Put before a reason that speaks of an abstract the record does not have.</summary>
    public const string AbstractClaimWarning = "(Warning: this reason mentions an abstract, but the record has none, so that part was not read from the record.)";

    /// <summary>
    /// The screening steps, shared by the systematic and the multivocal review so both screen with the same care
    /// (Garousi et al. G10). The second screener works through the criteria in a different order.
    /// </summary>
    public static string ScreeningProcedure(bool secondScreener) => secondScreener
        ? """
              You are the SECOND, INDEPENDENT screener. Work in this order:
              1. Check each exclusion threshold. If one clearly applies, the decision is Excluded.
              2. Otherwise decide Included only if the record gives positive evidence that it meets the inclusion thresholds.
              3. If the title and abstract are too thin to judge, decide on what is there and set confidence to "low".
              4. If Excluded, name the main reason: "exclusion-matched", "off-topic", "inclusion-not-met" or "too-little-information".
              """
        : """
              TASK:
              1. Determine if it should be Included or Excluded.
              2. Create a brief 1-2 sentence executive summary of the paper.
              3. Say how confident you are: "high", "medium" or "low" (low when the abstract is too thin to judge).
              4. If Excluded, name the main reason: "off-topic" (not about the review's subject), "inclusion-not-met" (on topic but misses an inclusion threshold), "exclusion-matched" (an exclusion threshold applies) or "too-little-information" (the record is too thin to show it qualifies).
              """;

    /// <summary>The answer format of a screening, shared by the systematic and the multivocal review.</summary>
    public const string ScreeningAnswerFormat = """
        Respond ONLY with a valid minified JSON object matching this structure exactly:
        {"decision":"Included or Excluded","reasoning":"Why it meets inclusion or hits exclusion parameters.","briefSummary":"The 1-2 sentence executive summary.","confidence":"high, medium or low","exclusionReason":"only when Excluded: off-topic, inclusion-not-met, exclusion-matched or too-little-information"}
        """;

    private async Task<bool> IsPeerReviewedAsync(IChatCompletionService chatService, string sourceName, AcademicPaper paper)
    {
        if (paper.Id.StartsWith("SCOPUS_ID:") || sourceName.Contains("ScienceDirect", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrEmpty(paper.JournalSource) && !paper.JournalSource.Contains("Preprint", StringComparison.OrdinalIgnoreCase);

        if (paper.Id.StartsWith("IEEE_") || sourceName.Contains("IEEE", StringComparison.OrdinalIgnoreCase))
            return true;

        if (paper.Id.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase) || sourceName.Contains("arXiv", StringComparison.OrdinalIgnoreCase))
        {
            string combinedMetadata = $"{paper.Title} {paper.JournalSource} {paper.Abstract}";
            bool hasHintOfPublication = combinedMetadata.Contains("Proceedings of", StringComparison.OrdinalIgnoreCase) ||
                                        combinedMetadata.Contains("Journal of", StringComparison.OrdinalIgnoreCase) ||
                                        combinedMetadata.Contains("Transactions on", StringComparison.OrdinalIgnoreCase) ||
                                        combinedMetadata.Contains("Published in", StringComparison.OrdinalIgnoreCase);
            return hasHintOfPublication && await VerifyPeerReviewStatusViaLLMAsync(chatService, paper);
        }

        return await VerifyPeerReviewStatusViaLLMAsync(chatService, paper);
    }

    private sealed class PeerReviewAnswer
    {
        public bool? IsPeerReviewed { get; set; }
    }

    private async Task<bool> VerifyPeerReviewStatusViaLLMAsync(IChatCompletionService chatService, AcademicPaper paper)
    {
        string record = $"- Title: {paper.Title}\n- Venue/Source: {paper.JournalSource}\n- Metadata Abstract Segment: {paper.Abstract}";
        var prompt = $$"""
            Analyze the following document metadata and determine if it has undergone formal peer review (e.g., published in an academic journal, peer-reviewed conference proceedings, or transactional series) or if it remains an un-reviewed preprint/working paper.

            {{PromptSafety.DataOnlyNotice}}

            Document Context:
            {{PromptSafety.Wrap(record, "document metadata")}}

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "isPeerReviewed": true } or { "isPeerReviewed": false }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<PeerReviewAnswer>(chatService, prompt, JsonMode(0.0),
                a => a.IsPeerReviewed == null ? "isPeerReviewed must be true or false." : null);
            return answer.IsPeerReviewed == true;
        }
        catch (Exception ex)
        {
            // Unknown status counts as not peer reviewed: the filter is only on when the user asked for it.
            _log.LogWarning("Peer-review check failed for '{Title}': {Message}", paper.Title, SanitizeLogMessage(ex.Message));
            return false;
        }
    }
}

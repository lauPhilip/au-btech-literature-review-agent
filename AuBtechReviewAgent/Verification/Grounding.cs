using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>How a review runs the grounding check on its text.</summary>
/// <param name="Parallelism">How many sources are checked at the same time.</param>
/// <param name="SecondCheck">Whether citations that are not fully supported get a second, independent check.</param>
/// <param name="Repair">Whether sentences with a rejected citation are repaired once and checked again.</param>
/// <param name="Review">How the prompts name the review, e.g. "systematic literature review".</param>
public sealed record GroundingOptions(
    int Parallelism = 1,
    bool SecondCheck = true,
    bool Repair = true,
    string Review = CitationRepairer.DefaultReview);

/// <summary>The checked (and possibly repaired) text, every verdict, the counts before the repair, and the repair log.</summary>
public sealed record GroundingResult(
    List<(string Field, string Text)> Fields,
    List<CitationSupportResult> Checks,
    CitationSupportSummary BeforeRepair,
    List<CitationRepair> Repairs);

/// <summary>
/// The grounding check, the heart of TraceableAI, shared by every kind of review. Every cited sentence of a text is
/// checked against the source it cites (a paper, or the text of any other source): the model must give a verdict
/// and a verbatim quote, and the quote is looked up in the source's text in code. Sentences whose citation is
/// rejected are repaired once and checked again. The check does not know which kind of review it serves; a module
/// passes its text, its numbered sources and how to name the review.
/// </summary>
public static class Grounding
{
    /// <summary>
    /// Checks every citation in the fields against its source, then repairs rejected citations once (when
    /// <see cref="GroundingOptions.Repair"/> is set). <paramref name="checking"/> is told how many citations will be
    /// checked, <paramref name="checkProgress"/> how far each pass has come, and <paramref name="repairing"/> how
    /// many citations go to the repair pass.
    /// </summary>
    public static async Task<GroundingResult> CheckAndRepairAsync(
        IChatCompletionService chat,
        List<(string Field, string Text)> fields,
        IReadOnlyList<ReferencedPaper> sources,
        IReadOnlyDictionary<int, IReadOnlyList<string>> verifiedQuotes,
        int referenceCount,
        GroundingOptions options,
        Action<int>? checking = null,
        Action<bool, int, int>? checkProgress = null,
        Action<int>? repairing = null)
    {
        var cited = fields.SelectMany(f => CitationSupportChecker.ExtractCitedSentences(f.Text, f.Field)).ToList();
        checking?.Invoke(cited.Sum(c => c.References.Count));

        List<CitationSupportResult> checks;
        using (LlmStage.Begin("citation-check"))
            checks = await CitationSupportChecker.CheckAsync(chat, cited, sources, verifiedFindings: verifiedQuotes,
                parallelism: options.Parallelism, secondCheck: options.SecondCheck, progress: checkProgress, review: options.Review);
        var beforeRepair = CitationSupportSummary.From(checks);

        var repairs = new List<CitationRepair>();
        if (options.Repair && checks.Any(CitationRepairer.NeedsRepair))
        {
            repairing?.Invoke(checks.Count(CitationRepairer.NeedsRepair));
            using (LlmStage.Begin("citation-repair"))
                (fields, checks, repairs) = await CitationRepairer.RepairAsync(chat, fields, checks, sources, verifiedQuotes, referenceCount,
                    options.Parallelism, options.SecondCheck, options.Review);
        }
        return new GroundingResult(fields, checks, beforeRepair, repairs);
    }

    /// <summary>
    /// Checks one citation again on request, after the run: more excerpts and the second check always on. Returns
    /// null when the model gave no answer.
    /// </summary>
    public static async Task<CitationSupportResult?> RecheckAsync(
        IChatCompletionService chat,
        CitedSentence sentence,
        ReferencedPaper source,
        IReadOnlyDictionary<int, IReadOnlyList<string>> verifiedQuotes,
        string review = CitationRepairer.DefaultReview)
    {
        List<CitationSupportResult> result;
        using (LlmStage.Begin("citation-recheck"))
            result = await CitationSupportChecker.CheckAsync(chat, new[] { sentence }, new[] { source },
                excerptsPerReference: 8, verifiedFindings: verifiedQuotes, parallelism: 1, secondCheck: true, review: review);
        return result.FirstOrDefault();
    }
}

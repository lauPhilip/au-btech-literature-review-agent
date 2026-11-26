using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>How a review runs the grounding check on its text.</summary>
/// <param name="Parallelism">How many sources are checked at the same time.</param>
/// <param name="SecondCheck">Whether citations that are not fully supported get a second, independent check.</param>
/// <param name="Repair">Whether sentences with a rejected citation are repaired and checked again, with a second pass for what is still flagged.</param>
/// <param name="Review">How the prompts name the review, e.g. "systematic literature review".</param>
/// <param name="Cache">Where the check's answers are kept, so the same sentence with the same evidence is not asked again; none by default.</param>
/// <param name="SenseCheck">Whether every repaired paragraph is read for sense after the repair, and rewritten once when it does not read well (<see cref="RepairSense"/>).</param>
public sealed record GroundingOptions(
    int Parallelism = 1,
    bool SecondCheck = true,
    bool Repair = true,
    string Review = CitationRepairer.DefaultReview,
    CheckCache? Cache = null,
    bool SenseCheck = true);

/// <summary>
/// The checked (and possibly repaired) text, every verdict, the counts before the repair, the repair log, and the
/// citations the second pass removed (no longer in <see cref="Checks"/>, so they are counted on their own), and the
/// counts after each step as one sequence (<see cref="CitationCheckSteps"/>).
/// </summary>
public sealed record GroundingResult(
    List<(string Field, string Text)> Fields,
    List<CitationSupportResult> Checks,
    CitationSupportSummary BeforeRepair,
    List<CitationRepair> Repairs,
    List<RemovedCitation> Removed,
    List<RepairSenseCheck>? Sense = null,
    CitationCheckSteps? Steps = null);

/// <summary>
/// The grounding check, the heart of TraceableAI, shared by every kind of review. Every cited sentence of a text is
/// checked against the source it cites (a paper, or the text of any other source): the model must give a verdict
/// and a verbatim quote, and the quote is looked up in the source's text in code. Sentences whose citation is
/// rejected are repaired and checked again; a second pass rewrites what is still partly supported and removes what
/// is still not supported, and every repaired paragraph is read for sense. The check does not know which kind of review it serves; a module
/// passes its text, its numbered sources and how to name the review.
/// </summary>
public static class Grounding
{
    /// <summary>
    /// Checks every citation in the fields against its source, then repairs rejected citations (when
    /// <see cref="GroundingOptions.Repair"/> is set), with a second pass when the first leaves citations flagged. <paramref name="checking"/> is told how many citations will be
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
        var original = fields;
        var cited = fields.SelectMany(f => CitationSupportChecker.ExtractCitedSentences(f.Text, f.Field)).ToList();
        checking?.Invoke(cited.Sum(c => c.References.Count));

        List<CitationSupportResult> checks;
        using (LlmStage.Begin("citation-check"))
            checks = await CitationSupportChecker.CheckAsync(chat, cited, sources, verifiedFindings: verifiedQuotes,
                parallelism: options.Parallelism, secondCheck: options.SecondCheck, progress: checkProgress, review: options.Review, cache: options.Cache);
        var beforeRepair = CitationSupportSummary.From(checks);
        var steps = CitationCheckSteps.AfterChecks(checks);
        int afterFirstRepair = checks.Count, afterSecondPass = checks.Count;
        bool repaired = false;

        var repairs = new List<CitationRepair>();
        var removed = new List<RemovedCitation>();
        if (options.Repair && checks.Any(CitationRepairer.NeedsRepair))
        {
            repaired = true;
            repairing?.Invoke(checks.Count(CitationRepairer.NeedsRepair));
            using (LlmStage.Begin("citation-repair"))
                (fields, checks, repairs) = await CitationRepairer.RepairAsync(chat, fields, checks, sources, verifiedQuotes, referenceCount,
                    options.Parallelism, options.SecondCheck, options.Review, cache: options.Cache);
            afterFirstRepair = afterSecondPass = checks.Count;
            if (checks.Any(CitationRepairer.NeedsRepair))
            {
                repairing?.Invoke(checks.Count(CitationRepairer.NeedsRepair));
                SecondPassResult second;
                using (LlmStage.Begin("citation-repair"))
                    second = await CitationRepairer.SecondPassAsync(chat, fields, checks, sources, verifiedQuotes, referenceCount,
                        options.Parallelism, options.SecondCheck, options.Review, options.Cache);
                (fields, checks, removed) = (second.Fields, second.Results, second.Removed);
                repairs.AddRange(second.Repairs);
                afterSecondPass = checks.Count;
            }
        }
        var sense = new List<RepairSenseCheck>();
        if (options.Repair && options.SenseCheck && repairs.Count > 0)
            (fields, checks, sense) = await RepairSense.CheckAsync(chat, original, fields, checks, repairs, sources, verifiedQuotes, options);
        return new GroundingResult(fields, checks, beforeRepair, repairs, removed, sense,
            steps.WithRepair(repaired, afterFirstRepair, afterSecondPass, checks.Count, repairs));
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

    /// <summary>
    /// Checks a sentence the reviewer edited after the run: one call per cited source on the fast stage of the first
    /// check, with no second check and no repair, since the reviewer's text is kept as written and only marked. The
    /// sentence is wrapped as data in the prompt. Returns one verdict per citation.
    /// </summary>
    public static async Task<List<CitationSupportResult>> CheckEditAsync(
        IChatCompletionService chat,
        CitedSentence sentence,
        IReadOnlyList<ReferencedPaper> sources,
        IReadOnlyDictionary<int, IReadOnlyList<string>> verifiedQuotes,
        string review = CitationRepairer.DefaultReview)
    {
        using (LlmStage.Begin("citation-check"))
            return await CitationSupportChecker.CheckAsync(chat, new[] { sentence }, sources, verifiedFindings: verifiedQuotes,
                parallelism: 1, secondCheck: false, review: review, reviewerText: true);
    }
}

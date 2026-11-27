using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>The certainty of the evidence behind one answer, as the certainty rule rated it in code; a null level is not rated.</summary>
public sealed record AnswerCertainty(string Question, string? Level);

/// <summary>
/// The front matter and the conclusion claim no more than the evidence allows, report no count of zero, and the rationale
/// argues instead of listing (issues #675 and #676). In run 8942b961 the abstract said "These findings show that effective
/// safety ... requires combining ..." from nine studies, four not empirical, with both answers of moderate certainty; it
/// reported "removed 0 duplicates"; and the rationale was one sentence per study, with the gap only in its last sentence.
/// The checks are soft: an answer is sent back at most twice for them, then kept with a note on file.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>The prompt block with the certainty of each answer, or empty when no answer is rated.</summary>
    public static string CertaintyBlock(IReadOnlyList<AnswerCertainty>? certainty) =>
        certainty is not { Count: > 0 } ? ""
            : "\n\nCERTAINTY OF THE EVIDENCE BEHIND EACH ANSWER (rated in code from the designs, appraisal and number of the sources; a general claim may be no stronger than this):\n" +
              string.Join("\n", certainty.Select(c => $"{c.Question}: {c.Level ?? "not rated"}"));

    /// <summary>Whether no answer is of high certainty, so every general claim must be hedged; false when no answer is rated.</summary>
    public static bool NoHighCertainty(IReadOnlyList<AnswerCertainty>? certainty) =>
        certainty != null && certainty.Any(c => c.Level != null) && !certainty.Any(c => c.Level == "high");

    /// <summary>The prompt rule that maps the certainty levels to wording, or empty when no answer is rated.</summary>
    public static string HedgeRule(IReadOnlyList<AnswerCertainty>? certainty, string part)
    {
        if (certainty == null || !certainty.Any(c => c.Level != null)) return "";
        string hedged = "with hedged words such as \"suggest\", \"may\" or \"point to\", never \"show that\", \"demonstrate\", \"prove\", \"establish\", \"confirm\", \"require\" or \"must\"";
        return (NoHighCertainty(certainty)
            ? $" No answer is of high certainty, so {part} states every general claim {hedged}."
            : $" In {part}, a claim that rests on an answer of high certainty may be stated firmly; a claim that rests on an answer of moderate, low or very low certainty is stated {hedged}.") + " " + ReportedFactRule;
    }

    /// <summary>
    /// The prompt rule that keeps a source's report on its own sample out of the hedge (issue #726): run 34d1af9a wrote
    /// "No evaluated platform may combine ...", which reads as a ban, for "none of the platforms it evaluated combined ...".
    /// </summary>
    public const string ReportedFactRule =
        "What a source reports about its own sample, such as that none of the tools it evaluated did something, is a fact, not a general claim: state it plainly in the past tense (\"none of the evaluated platforms combined ...\"), and never write \"no ... may\".";

    // "No evaluated platform may combine", "none of the tools may": an absence hedged into what reads as a ban.
    private static readonly Regex HedgedAbsence = new(
        @"\b(?:no|none\s+of\s+(?:the|these|those|its|their))\s+(?:(?!(?:that|which|whether|if)\b)[\p{L}-]+\s+){0,4}?may\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The first sentence that hedges a reported absence into "no ... may", or null (issue #726). Public for unit tests.</summary>
    public static string? HedgedAbsenceProblem(string? text, string part) =>
        CitationSupportChecker.SplitSentences(text ?? "").FirstOrDefault(s => HedgedAbsence.IsMatch(s)) is { } sentence
            ? $"{part}: \"{sentence.Trim()}\" reads as a ban (\"no ... may\"). If a source reports that none of what it studied did this, say so plainly in the past tense: \"none of the evaluated ... combined ...\"."
            : null;

    // A verb that states a claim as settled ("show that", "demonstrate", "requires").
    private static readonly Regex StrongClaim = new(
        @"\b(?:show(?:s|ed|n)?\s+that|demonstrat(?:e|es|ed|ing)|prov(?:e|es|ed|en)\s+that|establish(?:es|ed)?\s+that|confirm(?:s|ed)?\s+that|requires?|required|must)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The first sentence of <paramref name="text"/> that states a claim as settled while no answer is of high certainty,
    /// or null. Public for unit tests.
    /// </summary>
    public static string? OverclaimProblem(string? text, IReadOnlyList<AnswerCertainty>? certainty, string part)
    {
        if (!NoHighCertainty(certainty)) return null;
        foreach (var sentence in CitationSupportChecker.SplitSentences(text ?? ""))
            if (StrongClaim.Match(sentence) is { Success: true } verb)
                return $"{part}: \"{sentence.Trim()}\" states a claim as settled (\"{verb.Value}\"), but no answer is of high certainty ({string.Join(", ", certainty!.Select(c => $"{c.Question} {c.Level ?? "not rated"}"))}); write \"suggest\" or \"may\" instead.";
        return HedgedAbsenceProblem(text, part);
    }

    /// <summary>The prompt rule against reporting a count of zero.</summary>
    public const string ZeroCountRule = "Report no count of zero (\"removed 0 duplicates\"): leave it out, or write it in words (\"no duplicates were found\") when it matters.";

    // A count of zero in digits: "0 duplicates", "0 of the records", "removed 0".
    private static readonly Regex ZeroCount = new(
        @"(?<![\d.,])\b0\s+(?:(?:of\s+the\s+|\w+\s+)?(?:duplicates?|records?|studies|study|sources?|papers?|reports?|decisions?))\b|\b(?:removed|excluded|found|identified|screened|changed|retrieved|included)\s+0\b(?![.,]\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The first sentence that reports a count of zero in digits, or null (issue #676). Public for unit tests.</summary>
    public static string? ZeroCountProblem(string? text, string part)
    {
        var sentence = CitationSupportChecker.SplitSentences(text ?? "").FirstOrDefault(s => ZeroCount.IsMatch(s));
        return sentence == null ? null : $"{part}: \"{sentence.Trim()}\" reports a count of zero; {ZeroCountRule}";
    }

    /// <summary>The front-matter prompt's rule on the rationale (issue #675): an argument with the sources as its evidence.</summary>
    public static string RationaleRule(WritingProfile profile) =>
        $"\"rationale\": one or two paragraphs on why the review is needed, written as one argument, not as a list of {profile.Sources}: what is known, what is missing, and why that matters, in that order. " +
        $"Use the {profile.Sources} as evidence for each step of the argument: group {profile.Sources} that support the same point in one sentence, and never write one sentence per {profile.Source}. " +
        $"Every sentence but the last states a point of the argument from what the {profile.Sources} say and ends with an inline [n] marker, using the {profile.Sources} the results cite for it; these sentences use no result figure (no number, percentage or effect size from the results) and cite at most {MaxReferencesPerSentence} {profile.Sources} each, ideally one or two. " +
        $"The last sentence has no citation and states what is not yet known, the gap this review addresses; it names no {profile.Source}, number or {profile.Source} design and uses none of these words: report, found, show, demonstrate, observe, \"studies\", \"the literature\". " +
        "Add no background from general knowledge: leave out a sentence you cannot cite, since code removes every other rationale sentence without a citation.";

    /// <summary>A rationale is judged a list only from this many cited sentences on.</summary>
    public const int RationaleListingMinSentences = 4;

    /// <summary>
    /// Why the rationale reads as a list of one-source summaries, or null (issue #675): among at least
    /// <see cref="RationaleListingMinSentences"/> cited sentences, more than <see cref="ListingShare"/> each cite exactly one
    /// source, a different one from the sentence before. Public for unit tests.
    /// </summary>
    public static string? RationaleListingProblem(string? rationale, WritingProfile profile)
    {
        var cited = CitationSupportChecker.SplitSentences(rationale ?? "").Select(CitationSupportChecker.ReferencesIn).Where(r => r.Count > 0).ToList();
        if (cited.Count < RationaleListingMinSentences) return null;
        int listed = cited.Where((refs, i) => refs.Count == 1 && (i == 0 || !cited[i - 1].SequenceEqual(refs))).Count();
        if (listed <= cited.Count * ListingShare) return null;
        return $"{listed} of the rationale's {cited.Count} cited sentences each summarise one {profile.Source}, so it reads as a list. " +
               $"Write one argument instead: what is known, what is missing and why it matters, with the {profile.Sources} that support each point cited together.";
    }
}

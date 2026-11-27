using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Wording checks on model text (issues #746 and #747, run 866a897b): a sentence that opens on a numeral, a sentence whose
/// only claim is that something "is reported", another theme named with its title case in running text, and a cited
/// sentence that says what this review read its sources from. Each returns a message that sends the text back, or null.
/// The numeral, the empty-sentence and the theme-name checks are soft in the writers, so no text is lost to them.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>The prompt rule that no sentence opens on a numeral (issue #746).</summary>
    public const string NumeralOpeningRule = "Never open a sentence with a numeral: spell the number out or reword (\"Of the 16 studies, 12 ...\").";

    /// <summary>The prompt rule on the wording of a subsection (issue #746).</summary>
    public static string WordingRule(WritingProfile profile) =>
        $"Never open a sentence with a numeral: spell the number out or reword (\"Of the 16 {profile.Sources}, 12 ...\"). " +
        $"Every sentence says what a {profile.Source} found or means; never write a sentence whose only verb is \"is reported\" or \"is described\" (\"X is reported [1], and Y is reported [2].\"), since that says only that something exists. " +
        "When you refer to another theme, write its name in lower case, keeping acronyms (\"as reported under architectural and agent-level safety mechanisms\"), never with its capitals.";

    /// <summary>The prompt rule that keeps the review's own process out of cited sentences (issue #747).</summary>
    public static string ProcessFactsRule(WritingProfile profile) =>
        $"A sentence that cites a {profile.Source} says only what that {profile.Source} states. Facts about how this review was done, such as whether a {profile.Source} was read from its abstract only or in full text, are not in any {profile.Source}; the methods text reports them, so leave them out of every cited sentence.";

    /// <summary>A theme name as it is written inside a sentence: lower case, keeping acronyms and names with digits (issue #746).</summary>
    public static string ThemeNameInText(string? name) =>
        string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => ArticleKeywords.KeepsCapitals(w) ? w : w.ToLowerInvariant()));

    // A full stop after one of these is not the end of a sentence ("et al. 2020", "Fig. 3").
    private static readonly Regex Abbreviation = new(@"(?:^|\s)(?:al|fig|figs|no|nos|eq|eqs|vs|approx|ca|cf|p|pp|vol|sec|ch)\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The first sentence that opens on a numeral, as a message, or null. It splits the text itself, because
    /// <see cref="CitationSupportChecker.SplitSentences"/> does not start a sentence on a digit. Public for unit tests.
    /// </summary>
    public static string? NumeralOpeningProblem(string? text, string part)
    {
        var pieces = Regex.Split((text ?? "").Trim(), @"(?<=[.!?])\s+|\n+").Where(p => p.Length > 0).ToList();
        for (int i = 0; i < pieces.Count; i++)
            if (char.IsDigit(pieces[i][0]) && (i == 0 || !Abbreviation.IsMatch(pieces[i - 1])))
                return $"{part}: \"{pieces[i]}\" opens on a numeral; spell the number out or reword the sentence.";
        return null;
    }

    private static readonly Regex Marker = new(@"\s*\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex ClauseBreak = new(@"\s*(?:;|,\s*(?:and|while|but)\b)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OnlyReported = new(@"^\S+(?:\s+\S+)*\s+(?:is|are|was|were)\s+(?:also\s+)?(?:reported|described)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The first sentence whose only verb is "is reported" or "is described", as a message, or null (issue #746): each of its
    /// clauses, without markers, ends on that verb, so it says only that something exists. Public for unit tests.
    /// </summary>
    public static string? EmptyReportedProblem(string? text, string part)
    {
        foreach (var sentence in CitationSupportChecker.SplitSentences(text ?? ""))
        {
            string bare = Marker.Replace(sentence, "").Trim().TrimEnd('.', '!', '?', ' ');
            if (bare.Any(char.IsDigit)) continue; // a figure is a finding, however it is worded
            var clauses = ClauseBreak.Split(bare).Where(c => c.Length > 0).ToList();
            if (clauses.Count > 0 && clauses.All(c => OnlyReported.IsMatch(c.Trim().TrimEnd(','))))
                return $"{part}: \"{sentence}\" says only that something is reported or described. Say what it found or what it means, with the finding, or leave the sentence out.";
        }
        return null;
    }

    /// <summary>
    /// The first sentence that names another theme with the capitals of its heading, as a message, or null (issue #746). A
    /// name that is already lower case, or has no capital to lower, or opens the sentence, is not checked. Public for unit tests.
    /// </summary>
    public static string? OtherThemeCaseProblem(string? text, IEnumerable<string> otherThemeNames)
    {
        foreach (var sentence in CitationSupportChecker.SplitSentences(text ?? ""))
            foreach (var name in otherThemeNames.Where(n => !string.IsNullOrWhiteSpace(n)))
                if (ThemeNameInText(name) != name.Trim() && sentence.Length > 1 && sentence.IndexOf(name.Trim(), 1, StringComparison.Ordinal) > 0)
                    return $"\"{sentence}\" names another theme with its capitals; write \"{ThemeNameInText(name)}\" in running text, or name the section.";
        return null;
    }

    // What this review read a source from, in a sentence that carries a citation (issue #747).
    private static readonly Regex ReadingBasisWords = new(
        @"\babstracts?[- ]only\b|\bfull[- ]?texts?\b|\bonly\s+(?:the\s+|their\s+|its\s+)?abstracts?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The first cited sentence that mentions "abstract-only" or "full text", as a message, or null (issue #747). Whether a
    /// source was read from its abstract or in full is in the review's ledger, not in the source, so a citation after it
    /// makes the source look as if it said so. <see cref="ReadingBasisSentence"/> covers the sentences without a citation.
    /// Public for unit tests.
    /// </summary>
    public static string? CitedReadingBasisProblem(string? text, string part) =>
        CitationSupportChecker.SplitSentences(text ?? "").FirstOrDefault(s => ThematicSynthesis.CitedIn(s).Count > 0 && ReadingBasisWords.IsMatch(s)) is { } sentence
            ? $"{part}: \"{sentence}\" cites a source for what this review read it from (its abstract only or its full text). That is a fact about this review, not about the source, and the methods text reports it; leave it out of the sentence."
            : null;
}

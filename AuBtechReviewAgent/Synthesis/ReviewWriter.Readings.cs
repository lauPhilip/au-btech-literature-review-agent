using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Results that carry their figures, and a discussion that reads the results instead of retelling them (issue #572). In
/// run 289a0d3e a theme wrote "measurable gains in productivity, defect reduction, release cycle, and security compliance
/// [1]" although the extraction held the study's 38 %, 41 %, 47 % and 35 % with a verified quote; the discussion then
/// retold the two themes clause by clause and read "consistent directional benefits" into two results that measure
/// different things in different settings. The checks are soft (sent back at most twice, then kept with a note on file),
/// so a discussion is never lost to them; an uncited sentence that claims agreement is then left out in code.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>The prompt rule that a result is reported with its verified figures.</summary>
    public static string VerifiedFigureRule(WritingProfile profile) =>
        $"When a {profile.Source}'s verified finding gives figures (a percentage, an effect size, a fold change), report the result with those figures, as the finding states them; never write \"measurable gains\", \"significant improvements\" or the like in their place.";

    /// <summary>The discussion prompt's rule against retelling the results and reading agreement into them.</summary>
    public static string ReadingNotRetellingRule(WritingProfile profile) =>
        $"Do not retell the theme subsections: the results are printed above, so a paragraph that repeats them sentence by sentence is sent back. " +
        $"Claim that {profile.Sources} agree, converge or are consistent only when they measure the same outcome; results that measure different things in different settings neither agree nor conflict, so say what each measured instead. " +
        ProcessFactsRule(profile);

    // A result word with a vague size and no figure ("measurable gains", "significant improvements").
    private static readonly Regex VagueResult = new(
        @"\b(?:measurable|significant|substantial|considerable|notable|marked|meaningful|clear|large|strong)\s+(?:gains?|improvements?|reductions?|increases?|benefits?|effects?|savings?|speed-?ups?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A figure as a result gives it: a number with a percent sign, percentage points, a fold change or a multiplier.
    private static readonly Regex QuotedFigure = new(
        @"\d+(?:[.,]\d+)?\s*(?:%|percent\b|per\s*cent\b|percentage\s+points?\b|-?fold\b|x\b|×)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The first sentence of <paramref name="text"/> that reports a result for a source with a vague size and no figure
    /// while that source's verified finding gives figures, with the finding; null when there is none. Public for unit tests.
    /// </summary>
    public static string? UnquantifiedFindingProblem(string text, ReviewEvidence evidence)
    {
        foreach (var sentence in CitationSupportChecker.SplitSentences(text ?? ""))
        {
            if (!VagueResult.IsMatch(sentence) || QuotedFigure.IsMatch(Regex.Replace(sentence, @"\[[^\]]*\]", ""))) continue;
            foreach (int reference in ThematicSynthesis.CitedIn(sentence))
                if (evidence.Source(reference)?.Findings.FirstOrDefault(f => QuotedFigure.IsMatch(f.Quote)) is { } finding)
                    return $"\"{sentence.Trim()}\" reports a result of [{reference}] without its figures, though its verified finding gives them: \"{finding.Value}\" (quote: \"{finding.Quote}\"). Give the figures as the finding states them.";
        }
        return null;
    }

    // A claim that results agree, converge or are consistent.
    private static readonly Regex AgreementClaim = new(
        @"\b(?:consistent|convergent|coherent|uniform)\s+(?:directional\s+)?(?:benefits?|gains?|results?|findings?|evidence|patterns?|picture|effects?|direction)\b" +
        @"|\babsence\s+of\s+(?:any\s+)?(?:conflict|contradiction|disagreement)s?\b|\bno\s+(?:conflict|contradiction|disagreement)s?\s+between\b" +
        @"|\b(?:results|findings|studies|sources|evidence)\s+(?:all\s+)?(?:agree|converge|corroborate|concur|align|are\s+consistent|point\s+in\s+the\s+same\s+direction)\b" +
        @"|\bcorroborat\w*|\bmutually\s+reinforc\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The first sentence that claims the results agree or are consistent, or null. Public for unit tests.</summary>
    public static string? AgreementSentence(string text) =>
        CitationSupportChecker.SplitSentences(text ?? "").FirstOrDefault(s => AgreementClaim.IsMatch(s));

    /// <summary>
    /// Why a discussion reads wrongly, or null (issue #572): a paragraph that retells the results, a claim of agreement
    /// between results, a cited sentence on what this review read a source from, or a result given without the figures its source's verified finding has. Public for unit tests.
    /// </summary>
    public static string? DiscussionReadingProblem(string discussion, IEnumerable<string> themeTexts, ReviewEvidence evidence)
    {
        if (RetoldParagraph(discussion, themeTexts) is { } retold)
            return $"discussion paragraph {retold.Paragraph} retells the results (\"{retold.Sentence.Trim()}\"); the results are printed above, so say what they mean together instead of repeating them.";
        if (AgreementSentence(discussion) is { } agreement)
            return $"\"{agreement.Trim()}\" reads agreement or consistency into the results; results that measure different things in different settings neither agree nor conflict. Say what each measured, and claim agreement only between sources that measure the same outcome.";
        // Issue #747: what this review read a source from is in the ledger, not in the source.
        return CitedReadingBasisProblem(discussion, "the discussion") ?? UnquantifiedFindingProblem(discussion, evidence);
    }

    /// <summary>
    /// The first paragraph of <paramref name="discussion"/> (numbered from 1) in which at least two sentences, and at least
    /// half of all its sentences, retell a sentence of the results: most content words of the shorter of the two are in
    /// the other. Null when none does. Public for unit tests.
    /// </summary>
    public static (int Paragraph, string Sentence)? RetoldParagraph(string discussion, IEnumerable<string> themeTexts)
    {
        var results = themeTexts.SelectMany(t => CitationSupportChecker.SplitSentences(t ?? "")).Select(ContentWords).Where(w => w.Count >= 4).ToList();
        var paragraphs = Regex.Split((discussion ?? "").Trim(), @"\n\s*\n").Where(p => p.Trim().Length > 0).ToList();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            var sentences = CitationSupportChecker.SplitSentences(paragraphs[i]);
            var retold = sentences.Where(s => ContentWords(s) is { Count: >= 4 } words
                && results.Any(r => words.Count(r.Contains) >= 0.6 * Math.Min(words.Count, r.Count))).ToList();
            if (retold.Count >= 2 && retold.Count * 2 >= sentences.Count) return (i + 1, retold[0]);
        }
        return null;
    }

    /// <summary>
    /// The discussion without its uncited sentences that claim the results agree (issue #572): they are the review's own
    /// reading and cite nothing, so leaving them out loses no citation. <paramref name="note"/> lists them, or is null.
    /// </summary>
    public static string WithoutUncitedAgreement(string discussion, out string? note)
    {
        note = null;
        var dropped = new List<string>();
        var paragraphs = Regex.Split((discussion ?? "").Trim(), @"\n\s*\n").Select(p =>
        {
            var sentences = CitationSupportChecker.SplitSentences(p);
            var keep = sentences.Where(s => ThematicSynthesis.CitedIn(s).Count > 0 || !AgreementClaim.IsMatch(s)).ToList();
            dropped.AddRange(sentences.Except(keep));
            return keep.Count == sentences.Count ? p.Trim() : string.Join(" ", keep.Select(s => s.Trim()));
        }).Where(p => p.Length > 0).ToList();
        if (dropped.Count == 0) return discussion ?? "";
        note = $"The discussion claimed agreement between results without a citation; {(dropped.Count == 1 ? "the sentence was" : "the sentences were")} left out in code: " +
               string.Join(" ", dropped.Select(d => $"\"{d.Trim()}\""));
        return string.Join("\n\n", paragraphs);
    }
}

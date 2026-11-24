using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// What the sources found, kept apart from what the review makes of it. An audit of two real runs found that the
/// prompts asked for a citation on anything attributed to the literature, so the model cited its own reasoning in the
/// rationale, the discussion and the closing sections; the citation check flagged those sentences (about 80 % of all
/// flagged citations), and the repair turned proposals and readings into restatements of findings. Now a finding ends
/// with its citation, and the review's own reading is a separate sentence with no citation that code checks names no
/// study, number or design.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>
    /// The prompt rule that keeps a finding and the review's reading of it in separate sentences. It names the words
    /// <see cref="UncitedAttribution"/> rejects, so the model knows what code checks.
    /// </summary>
    public static string EvidenceThenReadingRule(WritingProfile profile) =>
        $"Keep what the {profile.Sources} found apart from what this review makes of it. A sentence that reports what a {profile.Source} found ends with its citation, such as [3] or [2, 5], using the {profile.Sources} the results cite for it. " +
        $"The review's own reading of those findings (what they mean, how they connect, what to do or to study next) goes in a separate sentence with no citation; that sentence names no {profile.Source}, no number other than this review's own counts and no {profile.Source} design, and uses none of these words: report, found, show, demonstrate, observe, \"studies\", \"the literature\". " +
        "Never cite a sentence for the review's own reasoning. Code rejects an uncited sentence that reports a finding, and the citation check flags a cited sentence that says more than its source.";

    /// <summary>The counts written as words that <see cref="OwnCount"/> reads, one to twenty ("four studies").</summary>
    private const string NumberWordPattern = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty";

    private static readonly string[] NumberWords = NumberWordPattern.Split('|');

    /// <summary>A count as digits: "four" is "4"; digits stay as they are.</summary>
    private static string AsDigits(string count)
    {
        int word = Array.FindIndex(NumberWords, w => w.Equals(count, StringComparison.OrdinalIgnoreCase));
        return word < 0 ? count : (word + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The counts in a text, in digits or as words from one to twenty, all as digits ("four" is "4").</summary>
    public static HashSet<string> CountsIn(string text)
    {
        var counts = NumbersIn(text);
        foreach (Match m in Regex.Matches(text, @"\b(?:" + NumberWordPattern + @")\b", RegexOptions.IgnoreCase)) counts.Add(AsDigits(m.Value));
        return counts;
    }

    private static readonly Regex AttributionCue = new(
        @"\b(?:report(?:s|ed|ing)?|found|show(?:s|ed|n|ing)?|demonstrat(?:e|es|ed|ing)|observ(?:e|es|ed|ing)|studies|the\s+literature|according\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DesignWord = new(
        @"\b(?:randomi[sz]ed|trials?|case[- ]stud(?:y|ies)|surveys?|experiments?|experimental|interviews?|cohorts?|qualitative|quantitative|mixed[- ]methods?|longitudinal|cross[- ]sectional|meta[- ]analys[ie]s|ethnograph\w*|observational)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StudiesAfterCount = new(
        @"\b(?:\d+|" + NumberWordPattern + @")\s+(?:(?:included|empirical|non-empirical|primary|of\s+the|of\s+these)\s+)*(?:\d+\s+|" + NumberWordPattern + @"\s+)?studies\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Why a text has an uncited sentence that reports a finding, or null. An uncited sentence is the review's own
    /// reading, so it may not use a number written in digits that is not in <paramref name="facts"/> (the review's
    /// own counts), a word that attributes something to the sources (report, found, show, demonstrate, observe,
    /// "studies", "the literature", "according to"), or a study-design word. "Studies" after a count ("four
    /// studies") is the review's own count, not an attribution. <paramref name="designsAllowed"/> lets the
    /// limitations and the directions for future research name kinds of study (what the evidence rests on, what study is
    /// missing) without a citation. Public for unit tests.
    /// </summary>
    public static string? UncitedAttribution(string text, string facts = "", bool designsAllowed = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var known = NumbersIn(facts ?? "");
        foreach (var sentence in CitationSupportChecker.SplitSentences(text))
        {
            if (ThematicSynthesis.CitedIn(sentence).Count > 0) continue;
            string why = SentenceAttribution(sentence, known, designsAllowed) ?? "";
            if (why.Length > 0)
                return $"\"{sentence.Trim()}\" has no citation but {why}. A finding ends with the [n] of the sources the results cite for it; the review's own reading is a separate sentence that names no source, number or design.";
        }
        return null;
    }

    /// <summary>Why one uncited sentence reads as a finding, or null.</summary>
    private static string? SentenceAttribution(string sentence, HashSet<string> knownNumbers, bool designsAllowed)
    {
        var stray = NumbersIn(sentence).Where(n => !knownNumbers.Contains(n)).ToList();
        if (stray.Count > 0) return $"uses {string.Join(", ", stray)}, which is not one of this review's own counts";
        if (!designsAllowed && DesignWord.Match(sentence) is { Success: true } design && !OwnCount.IsMatch(sentence))
            return $"names a study design (\"{design.Value}\")";
        string plain = DesignWord.Replace(StudiesAfterCount.Replace(sentence, ""), "");
        var cue = AttributionCue.Matches(plain).FirstOrDefault(m => !(designsAllowed && m.Value.Equals("studies", StringComparison.OrdinalIgnoreCase)));
        return cue == null ? null : $"attributes it to the sources (\"{cue.Value}\")";
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Checks 9 and 10: whether the closing parts use the review's own terms, and how hard each part is to read. Both are
/// notes only: a term can be paraphrased well, and academic prose is hard to read by nature, so neither is a fault.
/// </summary>
public static partial class PaperSelfCheck
{
    /// <summary>The longest sentence, in words, that reads comfortably; longer ones are listed.</summary>
    public const int LongSentenceWords = 40;

    /// <summary>A Flesch reading ease below this is very hard even for academic prose (which mostly scores 10 to 40).</summary>
    public const double HardToRead = 10;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "above", "after", "again", "also", "among", "and", "are", "based", "been", "being", "between", "both", "does", "doing", "during",
        "each", "from", "have", "having", "into", "more", "most", "other", "over", "same", "such", "than", "that", "their", "them", "then", "there",
        "these", "they", "this", "those", "through", "under", "used", "uses", "using", "were", "what", "when", "where", "which", "while", "with",
        "within", "would", "how", "why", "who", "whom", "whose", "approaches", "aspects", "factors", "role", "roles", "towards",
    };

    private static readonly Regex Word = new(@"\p{L}[\p{L}'’-]*", RegexOptions.Compiled);

    /// <summary>The content words of a text, each cut to its first five letters, so "evaluation" and "evaluated" meet.</summary>
    internal static HashSet<string> Stems(string text, int minLength = 4) =>
        Word.Matches(text ?? "").Select(m => m.Value.ToLowerInvariant().Trim('\'', '’', '-'))
            .Where(w => w.Length >= minLength && !StopWords.Contains(w))
            .Select(w => w.Length > 5 ? w[..5] : w).ToHashSet();

    /// <summary>Check 9: the theme names come back in the discussion, the answers or the conclusion, and the questions' own terms in the answers or the conclusion.</summary>
    internal static PaperCheck TermsCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "terms",
            Title = "Key terms used consistently",
            Explanation = "The discussion, the answers and the conclusion should speak of the same themes the results name, and the answers should use the words of the questions, " +
                "so a reader can follow one thread from question to theme to answer. A theme can be paraphrased well, so this is a note, not a fault.",
        };
        var closing = Stems(string.Join(" ", input.Of(PaperPartKind.Discussion, PaperPartKind.Answer, PaperPartKind.Conclusion).Select(p => p.Text)));
        int named = 0;
        foreach (var theme in input.Themes)
        {
            var stems = Stems(theme.Name);
            if (stems.Count == 0) continue;
            int found = stems.Count(closing.Contains);
            if (found * 2 >= stems.Count) { named++; continue; }
            check.Evidence.Add(new("Theme: " + theme.Name, "", theme.Name, "The discussion, the answers and the conclusion do not name this theme."));
        }
        var answered = Stems(string.Join(" ", input.Of(PaperPartKind.Answer, PaperPartKind.Conclusion).Select(p => p.Text)));
        foreach (var q in input.Questions)
        {
            var words = Word.Matches(q.Question).Select(m => m.Value).Where(w => w.Length >= 5 && !StopWords.Contains(w))
                .Where(w => !answered.Contains(Stems(w, 5).FirstOrDefault() ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (words.Count > 0)
                check.Evidence.Add(new(q.Id, "", Short(q.Question), $"Not used in the answers or the conclusion: {string.Join(", ", words)}."));
        }
        check.Outcome = check.Evidence.Count == 0 ? CheckOutcome.Pass : CheckOutcome.Note;
        check.Summary = input.Themes.Count == 0 && input.Questions.Count == 0 ? "The paper has no themes or questions to follow."
            : $"{named} of {Plural(input.Themes.Count, "theme is", "themes are")} named again after the results" +
              (input.Questions.Count == 0 ? "." : $"; {input.Questions.Count - check.Evidence.Count(e => !e.Part.StartsWith("Theme"))} of {Plural(input.Questions.Count, "question", "questions")} keep their terms in the answers.");
        return check;
    }

    /// <summary>The syllables of one English word, by its vowel groups; good enough for a reading score.</summary>
    public static int Syllables(string word)
    {
        string w = Regex.Replace(word.ToLowerInvariant(), "[^a-z]", "");
        if (w.Length <= 3) return 1;
        w = Regex.Replace(w, "(?:[^laeiouy]es|ed|[^laeiouy]e)$", "");
        w = Regex.Replace(w, "^y", "");
        return Math.Max(1, Regex.Matches(w, "[aeiouy]{1,2}").Count);
    }

    /// <summary>The Flesch reading ease of a text, without its citation markers; null for a text without words.</summary>
    public static double? FleschReadingEase(string text)
    {
        string plain = Regex.Replace(text ?? "", @"\[[^\]]*\]", "");
        var sentences = CitationSupportChecker.SplitSentences(plain).Count;
        var words = Word.Matches(plain).Select(m => m.Value).ToList();
        if (sentences == 0 || words.Count == 0) return null;
        return Math.Round(206.835 - 1.015 * words.Count / sentences - 84.6 * words.Sum(Syllables) / words.Count, 1);
    }

    /// <summary>Check 10: the Flesch reading ease of each written part, and the sentences longer than <see cref="LongSentenceWords"/> words.</summary>
    internal static PaperCheck ReadabilityCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "readability",
            Title = "Readability",
            Explanation = "The Flesch reading ease of each part: 60 and above is plain English, 30 to 60 is fairly hard, and academic prose mostly scores 10 to 40. " +
                $"Each part also gives its mean sentence length, the share of its sentences over the {PlainEnglish.TargetSentenceWords}-word target of plain technical English and the share in the passive voice. " +
                $"A part below {HardToRead.ToString(CultureInfo.InvariantCulture)} and any sentence of more than {LongSentenceWords} words are listed as notes, because they are hard to follow.",
        };
        var scores = new List<double>();
        bool hard = false;
        foreach (var part in input.Written.Where(p => !IsEmpty(p.Text)))
        {
            if (FleschReadingEase(part.Text) is not { } score) continue;
            scores.Add(score);
            hard |= score < HardToRead;
            check.Evidence.Add(new(part.Name, part.Field, $"Reading ease {score.ToString("0", CultureInfo.InvariantCulture)}",
                (score >= 60 ? "Plain English." : score >= 30 ? "Fairly hard." : score >= HardToRead ? "Hard, as academic prose often is." : "Very hard to read.") +
                Measured(PlainEnglish.Measure(part.Text))));
        }
        int longOnes = 0;
        foreach (var part in input.Written)
            foreach (var sentence in CitationSupportChecker.SplitSentences(part.Text))
            {
                int words = Word.Matches(Regex.Replace(sentence, @"\[[^\]]*\]", "")).Count;
                if (words <= LongSentenceWords) continue;
                longOnes++;
                check.Evidence.Add(new(part.Name, part.Field, Short(sentence), $"A sentence of {words} words."));
            }
        check.Outcome = hard || longOnes > 0 ? CheckOutcome.Note : CheckOutcome.Pass;
        check.Summary = scores.Count == 0 ? "No written text to score."
            : $"Reading ease from {scores.Min().ToString("0", CultureInfo.InvariantCulture)} to {scores.Max().ToString("0", CultureInfo.InvariantCulture)} across {Plural(scores.Count, "part", "parts")}" +
              (PlainEnglish.Measure(string.Join("\n\n", input.Written.Select(p => p.Text))) is { } all ? string.Create(CultureInfo.InvariantCulture,
                  $"; mean sentence length {all.MeanSentenceWords:0.#} words, {all.ShareOver25Words:0%} of sentences over {PlainEnglish.TargetSentenceWords} words and {all.SharePassive:0%} in the passive voice") : "") +
              (longOnes > 0 ? $"; {Plural(longOnes, "sentence is", "sentences are")} longer than {LongSentenceWords} words." : ".");
        return check;
    }

    /// <summary>The plain-English measurements of a text as one sentence after a space; empty for a text without sentences.</summary>
    internal static string Measured(PlainEnglishMeasures? m) => m == null ? "" : string.Create(CultureInfo.InvariantCulture,
        $" Mean sentence length {m.MeanSentenceWords:0.#} words; {m.ShareOver25Words:0%} of sentences over {PlainEnglish.TargetSentenceWords} words; {m.SharePassive:0%} in the passive voice.");
}

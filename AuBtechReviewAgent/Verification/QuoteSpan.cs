using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The second half of the quote check (#606). The canonical form keeps only letters and digits, so on its own it
/// accepts a "quote" that runs from the end of one sentence into the next, or one stitched together across a heading
/// or a paragraph. After a canonical match, the matched stretch is read again in the original text and accepted only
/// when it is one run of text: no sentence end, no paragraph break and no long stretch of other characters that the
/// quote does not have. What the canonical form forgives in PDF text (line-break hyphens, ligatures, soft hyphens,
/// spacing, curly quotes) is only punctuation and spacing, so it passes here too.
/// </summary>
public static partial class QuoteSpan
{
    /// <summary>The longest gap an ellipsis may stand for: about two sentences.</summary>
    public const int MaxElidedCharacters = 400;

    /// <summary>
    /// True when the parts of a quote (split at its ellipses, each with its canonical form) are found in order in the
    /// source, each as one contiguous run of text, and each gap between two parts short and inside one paragraph.
    /// Every occurrence of the first part is tried, so a phrase that also appears in a heading is still found.
    /// </summary>
    public static bool Place(IReadOnlyList<(string Raw, string Canon)> parts, CanonicalText source)
    {
        string text = source.Value;
        if (source.Map is not { } map) return InOrder(parts, text);
        string original = source.Original;
        for (int at = text.IndexOf(parts[0].Canon, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(parts[0].Canon, at + 1, StringComparison.Ordinal))
        {
            if (!Contiguous(parts[0].Raw, original, map, at, parts[0].Canon.Length)) continue;
            if (PlaceRest(parts, original, text, map, at + parts[0].Canon.Length)) return true;
        }
        return false;
    }

    private static bool PlaceRest(IReadOnlyList<(string Raw, string Canon)> parts, string original, string text,
        (int[] Start, int[] End) map, int from)
    {
        for (int k = 1; k < parts.Count; k++)
        {
            int rawEnd = map.End[from - 1];
            int at = text.IndexOf(parts[k].Canon, from, StringComparison.Ordinal);
            for (; at >= 0; at = text.IndexOf(parts[k].Canon, at + 1, StringComparison.Ordinal))
            {
                // A later occurrence only widens the gap, so the first one over the limit ends the search.
                string gap = original[rawEnd..map.Start[at]];
                if (gap.Length > MaxElidedCharacters || ParagraphBreak().IsMatch(gap)) return false;
                if (Contiguous(parts[k].Raw, original, map, at, parts[k].Canon.Length)) break;
            }
            if (at < 0) return false;
            from = at + parts[k].Canon.Length;
        }
        return true;
    }

    // The canonical match alone, used when the text cannot be mapped back (see CanonicalText.Map).
    private static bool InOrder(IReadOnlyList<(string Raw, string Canon)> parts, string text)
    {
        int from = 0;
        foreach (var part in parts)
        {
            int at = text.IndexOf(part.Canon, from, StringComparison.Ordinal);
            if (at < 0) return false;
            from = at + part.Canon.Length;
        }
        return true;
    }

    /// <summary>
    /// True when the original text matched by canonical characters [at, at + length) reads as the quote does: it has
    /// no more sentence ends or paragraph breaks than the quote, and is not much longer once spacing is evened out.
    /// </summary>
    public static bool Contiguous(string quote, string original, (int[] Start, int[] End) map, int at, int length)
    {
        string span = original[map.Start[at]..map.End[at + length - 1]];
        if (SentenceEnds(span) > SentenceEnds(quote)) return false;
        if (ParagraphBreak().Count(span) > ParagraphBreak().Count(quote)) return false;
        int quoteLength = Even(quote).Length;
        return Even(span).Length <= quoteLength + quoteLength / 4 + 20;
    }

    /// <summary>
    /// Sentence ends: ".", "!" or "?" (after any closing quote or bracket), white space, then a capital or a digit.
    /// A full stop after a single letter ("J. Smith", "e.g. The") or a common abbreviation ("et al. Smith") is not one.
    /// </summary>
    public static int SentenceEnds(string text) =>
        SentenceEnd().Matches(text).Count(m => m.Groups[2].Value != "." || !Abbreviation(m.Groups[1].Value));

    private static bool Abbreviation(string word) =>
        word.Length == 1 || Abbreviations.Contains(word.ToLowerInvariant());

    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "al", "fig", "figs", "eq", "eqs", "cf", "vs", "no", "nos", "vol", "pp", "ref", "refs", "sec", "ch",
        "dr", "mr", "mrs", "ms", "prof", "st", "approx", "ca", "resp", "inc", "ltd", "co", "corp", "jr", "sr", "ed", "eds",
    };

    // Soft hyphens dropped and every run of white space read as one space.
    private static string Even(string text) => Spaces().Replace(text.Replace("­", ""), " ").Trim();

    [GeneratedRegex(@"(\p{L}*)([.!?])[\p{Pf}\p{Pe}'""]*\s+(?=[\p{Pi}\p{Ps}'""]*[\p{Lu}\p{Nd}])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\n[^\S\n]*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

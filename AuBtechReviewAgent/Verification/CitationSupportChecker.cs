using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Closes the gap the range validator leaves open: a citation can point at a real reference that does not
/// actually say what the sentence claims. For every cited reference, the sentences citing it are sent to the
/// model together with the most relevant excerpts of that paper, and the model must answer per sentence
/// with a verdict and a verbatim quote. The quote is then checked against the excerpt in code.
/// Nothing is removed from the report; the results are an audit for the human reviewer.
/// </summary>
public static partial class CitationSupportChecker
{
    public const string Supported = "supported";
    public const string Partial = "partially_supported";
    public const string NotSupported = "not_supported";
    public const string Unverifiable = "unverifiable";

    /// <summary>Part of the cache key of a check: raise it whenever the check prompt changes.</summary>
    public const string CheckPromptVersion = "citation-check-v1";

    /// <summary>A model answer kept in the cache, as received.</summary>
    public sealed class CachedAnswer { public string Text { get; set; } = ""; }
    public const string InsufficientEvidence = "insufficient_evidence";

    /// <summary>Rank of a verdict for the second-check rule: only verified support counts.</summary>
    public static int Rank(string verdict) => verdict switch { Supported => 2, Partial => 1, _ => 0 };

    /// <summary>
    /// The part of a sentence attributed to one reference: the text between the previous citation marker (or
    /// the start of the sentence) and the marker that cites it. Text after the last marker is the review's own
    /// interpretation and is not attributed to any paper. Falls back to the whole sentence when the clause is
    /// too short to stand alone (e.g. "Studies [3] show ...").
    /// </summary>
    public static string AttributedClause(string sentence, int reference)
    {
        var markers = MarkerPattern.Matches(sentence).Cast<Match>().ToList();
        string Plain(string t) => Regex.Replace(MarkerPattern.Replace(t, ""), @"\s{2,}", " ").Trim();
        int index = markers.FindIndex(m => ReferencesIn(m.Value).Contains(reference));
        if (index < 0) return Plain(sentence);
        int start = index == 0 ? 0 : markers[index - 1].Index + markers[index - 1].Length;
        string clause = sentence[start..markers[index].Index];
        clause = Regex.Replace(clause, @"^[\s,;:.\u2014\u2013-]*(and|or|while|whereas|but|yet|with|from|to|as well as)?\s+", "", RegexOptions.IgnoreCase);
        clause = Plain(clause).Trim(' ', ',', ';', ':', '\u2014', '\u2013', '-');
        if (clause.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
            clause = Plain(sentence[..markers[index].Index]).Trim(' ', ',', ';', ':');
        if (clause.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4) clause = Plain(sentence);
        return clause;
    }

    /// <summary>Text after the last citation marker (three words or more): the review's own interpretation.</summary>
    public static string? UnattributedTail(string sentence)
    {
        var markers = MarkerPattern.Matches(sentence).Cast<Match>().ToList();
        if (markers.Count == 0) return null;
        var last = markers[^1];
        string tail = sentence[(last.Index + last.Length)..].Trim().Trim(',', ';', ':', '.', ' ', '\u2014', '\u2013', '-');
        return tail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 ? tail : null;
    }

    private static readonly Regex MarkerPattern = new(@"\[(\d+(?:\s*[,–-]\s*\d+)*)\]", RegexOptions.Compiled);

    /// <summary>Splits text into sentences and returns the ones carrying [n] markers (ranges like [2-4] are expanded).</summary>
    public static IReadOnlyList<CitedSentence> ExtractCitedSentences(string text, string field)
    {
        var result = new List<CitedSentence>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var sentences = SplitSentences(text);
        for (int i = 0; i < sentences.Count; i++)
        {
            var refs = ReferencesIn(sentences[i]);
            if (refs.Count > 0) result.Add(new CitedSentence(field, i, sentences[i], refs));
        }
        return result;
    }

    /// <summary>
    /// The sentence split used by the check. The Review Output page uses the same split, so a clicked [n] in
    /// sentence i finds the verdict recorded for sentence i.
    /// </summary>
    public static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        // Split after ., ! or ? (optionally following a citation marker) when the next sentence starts.
        return Regex.Split(text.Trim(), @"(?<=[.!?])\s+(?=[A-Z\[(""])").Select(s => s.Trim()).ToList();
    }

    /// <summary>Reference numbers cited in one marker text or sentence; ranges like [2-4] are expanded.</summary>
    public static IReadOnlyList<int> ReferencesIn(string sentence)
    {
        var refs = new SortedSet<int>();
        foreach (Match m in MarkerPattern.Matches(sentence))
        {
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var range = part.Split(new[] { '-', '–' }, StringSplitOptions.TrimEntries);
                if (range.Length == 2 && int.TryParse(range[0], out int a) && int.TryParse(range[1], out int b) && b >= a && b - a < 50)
                    for (int n = a; n <= b; n++) refs.Add(n);
                else if (int.TryParse(part.Trim(), out int single))
                    refs.Add(single);
            }
        }
        return refs.ToList();
    }

    /// <summary>Splits a sentence into plain text and citation markers, in order (for rendering clickable markers).</summary>
    public static IReadOnlyList<(string Text, bool IsMarker)> SplitMarkers(string sentence)
    {
        var parts = new List<(string, bool)>();
        int pos = 0;
        foreach (Match m in MarkerPattern.Matches(sentence))
        {
            if (m.Index > pos) parts.Add((sentence[pos..m.Index], false));
            parts.Add((m.Value, true));
            pos = m.Index + m.Length;
        }
        if (pos < sentence.Length) parts.Add((sentence[pos..], false));
        return parts;
    }

    /// <summary>True when the quote (whitespace/case-insensitive, at least 20 characters) occurs in the excerpt.</summary>
    public static bool QuoteOccursIn(string? quote, string? excerpt) => QuoteOccursIn(quote, CanonicalText.Of(excerpt));

    /// <summary>
    /// The same check against a text made canonical once (<see cref="CanonicalText.Of"/>): a source checked against
    /// many quotes is normalised once instead of once per quote, which on a long page is most of the cost.
    /// </summary>
    public static bool QuoteOccursIn(string? quote, CanonicalText excerpt)
    {
        if (string.IsNullOrWhiteSpace(quote) || excerpt.IsBlank) return false;
        string text = excerpt.Value;

        // Models often shorten a quote with "..." (seen in real runs); every part must then occur, in order.
        // Parts shorter than 10 letters are ignored, so an ellipsis cannot turn a quote into loose keywords.
        // A joining word after an ellipsis ("... and use tools") is the model's glue, not part of the source.
        var parts = Ellipsis().Split(quote)
            .Select((p, i) => i == 0 ? p : Glue().Replace(p, ""))
            .Select(Canonical).Where(p => p.Length > 0).ToList();
        if (parts.Count > 1) parts = parts.Where(p => p.Length >= 10).ToList();
        if (parts.Count == 0 || parts.Sum(p => p.Length) < 15) return false;

        int from = 0;
        foreach (var part in parts)
        {
            int at = text.IndexOf(part, from, StringComparison.Ordinal);
            if (at < 0) return false;
            from = at + part.Length;
        }
        return true;
    }

    [GeneratedRegex(@"\.{3}|…")]
    private static partial Regex Ellipsis();

    [GeneratedRegex(@"^[\s,;:]*(and|or|but)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Glue();

    /// <summary>
    /// Letters and digits only, lower case, after Unicode normalisation. PDF text has line-break hyphens,
    /// ligatures ("ﬁ"), odd quotes and spacing that a model does not reproduce; comparing only the letters
    /// and digits makes a faithful quote match while a paraphrase still does not.
    /// </summary>
    public static string Canonical(string s)
    {
        string text = s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}

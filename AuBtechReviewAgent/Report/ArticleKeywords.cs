using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The keywords on the front page of a printed report, derived in code from what the run recorded, with no model call.
/// The rule, also on wiki page 06: the protocol's topic comes first; then a theme name and a search-string phrase take
/// turns, so the keywords say both what was looked for and what was found. A search string is split into phrases at its
/// Boolean operators, brackets, commas and semicolons, and loses its quotes, wildcards and field tags. A phrase is kept
/// when it has one to five words and is not only stop words. A phrase is dropped when it repeats one already kept, word
/// for word or inside it (ignoring case and a plural "s"). At most <see cref="Max"/> are kept; a run with few themes and
/// one search string has fewer than <see cref="Min"/>, and the front page shows what there is rather than inventing more.
/// </summary>
public static class ArticleKeywords
{
    public const int Min = 4;
    public const int Max = 7;
    private const int MaxWords = 5;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Separators = new(@"(?:^|\s+)(?:AND\s+NOT|AND|OR|NOT)\s+|[()\[\]{};,|]", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex FieldTag = new(@"\b[A-Z][A-Z-]{2,}\s*(?=\()|\b[a-z]{2,}:", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Junk = new(@"[""“”*?~^]", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Space = new(@"\s+", RegexOptions.CultureInvariant, Timeout);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "for", "in", "on", "to", "with", "by", "or", "not", "at", "from", "as", "is", "are", "how", "what", "which",
    };

    /// <summary>The keywords, in the order of the rule above.</summary>
    /// <param name="topic">The protocol's topic (the systematic review's search query, the multivocal plan's topic).</param>
    /// <param name="searchStrings">The search strings that were run, in order.</param>
    /// <param name="themes">The names of the synthesis's themes, in order.</param>
    public static IReadOnlyList<string> From(string? topic, IEnumerable<string>? searchStrings, IEnumerable<string>? themes)
    {
        var kept = new List<string>();
        var keys = new List<string>();
        void Offer(string phrase)
        {
            if (kept.Count >= Max) return;
            string shown = Shown(phrase);
            if (shown.Length == 0) return;
            string key = " " + Key(shown) + " ";
            if (keys.Any(k => k.Contains(key, StringComparison.Ordinal))) return;
            kept.Add(shown);
            keys.Add(key);
        }

        foreach (var phrase in Phrases(topic)) Offer(phrase);
        var themeList = (themes ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var stringPhrases = (searchStrings ?? Array.Empty<string>()).SelectMany(Phrases).ToList();
        for (int i = 0; i < Math.Max(themeList.Count, stringPhrases.Count) && kept.Count < Max; i++)
        {
            if (i < themeList.Count) Offer(themeList[i]);
            if (i < stringPhrases.Count) Offer(stringPhrases[i]);
        }
        return kept;
    }

    /// <summary>A search string's phrases: split at Boolean operators and brackets, without quotes, wildcards or field tags.</summary>
    public static IEnumerable<string> Phrases(string? searchString)
    {
        if (string.IsNullOrWhiteSpace(searchString)) yield break;
        string text = FieldTag.Replace(searchString, " ");
        foreach (var part in Separators.Split(text))
        {
            string phrase = Space.Replace(Junk.Replace(part, " "), " ").Trim();
            if (phrase.Length > 0) yield return phrase;
        }
    }

    // A word keeps its capitals when it is an acronym or a name with a digit (AI, LLMs, GPT-4); every other word is lower case.
    private static string Shown(string phrase)
    {
        var words = Space.Split(Junk.Replace(phrase, " ").Trim()).Where(w => w.Length > 0).ToList();
        if (words.Count == 0 || words.Count > MaxWords || words.All(StopWords.Contains)) return "";
        if (words.Sum(w => w.Length) < 3) return "";
        return string.Join(' ', words.Select(w => w.Count(char.IsUpper) > 1 || w.Any(char.IsDigit) ? w : w.ToLowerInvariant()));
    }

    // For comparing: lower case, each word without a final plural "s".
    private static string Key(string shown) =>
        string.Join(' ', shown.ToLowerInvariant().Split(' ').Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) ? w[..^1] : w));
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The Flesch Reading Ease of a text, the C# twin of reading_ease in tools/ReleaseCheck/release_notes.py: the same
/// sentence split, the same words and the same syllable estimate, so a page and the release notes meet one bar.
/// 206.835 - 1.015 x (words per sentence) - 84.6 x (syllables per word). Higher is easier; 60 is plain English.
/// Emoji are not removed here, because the pages that use it have none.
/// </summary>
public static partial class ReadingEase
{
    /// <summary>The bar the release notes and the API documentation page must reach.</summary>
    public const double Minimum = 60.0;

    /// <summary>The score of the text, or null when it has no words.</summary>
    public static double? Score(string markdown)
    {
        var sentences = Sentences(markdown);
        var words = sentences.SelectMany(Words).ToList();
        if (words.Count == 0) return null;
        int syllables = words.Sum(CountSyllables);
        return 206.835 - 1.015 * ((double)words.Count / sentences.Count) - 84.6 * ((double)syllables / words.Count);
    }

    /// <summary>
    /// The text as sentences of plain text. Headings are left out; a list item or a paragraph without a full stop
    /// still ends a sentence; links keep their words and lose their address; code marks and emphasis go.
    /// </summary>
    public static List<string> Sentences(string markdown)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        foreach (string line in markdown.Split('\n'))
        {
            string stripped = line.Trim();
            var item = ListItem().Match(stripped);
            if (stripped.Length == 0 || stripped.StartsWith('#') || item.Success)
            {
                if (current.Count > 0) blocks.Add(string.Join(" ", current));
                current = new List<string>();
                if (item.Success) current.Add(stripped[item.Length..]);
                continue;
            }
            current.Add(stripped);
        }
        if (current.Count > 0) blocks.Add(string.Join(" ", current));

        var sentences = new List<string>();
        foreach (string block in blocks)
        {
            string text = Link().Replace(block, "$1");
            text = Address().Replace(text, " ");
            text = Marks().Replace(text, "");
            // A full stop, question or exclamation mark followed by a space ends a sentence; "v0.1.0" does not.
            sentences.AddRange(SentenceEnd().Split(text).Where(s => Words(s).Count > 0));
        }
        return sentences;
    }

    public static List<string> Words(string text) => Word().Matches(text).Select(m => m.Value).ToList();

    /// <summary>
    /// An estimate of the syllables in an English word: groups of vowels, less a silent final e, at least one.
    /// A number, or a version such as v0.1.0, counts as one. Parts joined by a hyphen are counted one by one.
    /// </summary>
    public static int CountSyllables(string word)
    {
        var parts = word.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1) return parts.Sum(CountSyllables);
        string letters = NotLetter().Replace(word.ToLowerInvariant(), "");
        if (letters.Length <= 3) return 1;
        letters = SilentEnd().Replace(letters, "");
        letters = LeadingY().Replace(letters, "");
        return Math.Max(1, VowelGroup().Matches(letters).Count);
    }

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")] private static partial Regex ListItem();
    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"https?://\S+")] private static partial Regex Address();
    [GeneratedRegex("[`*_]")] private static partial Regex Marks();
    [GeneratedRegex(@"(?<=[.!?])\s+")] private static partial Regex SentenceEnd();
    [GeneratedRegex(@"[A-Za-z0-9]+(?:['’.-][A-Za-z0-9]+)*")] private static partial Regex Word();
    [GeneratedRegex("[^a-z]")] private static partial Regex NotLetter();
    [GeneratedRegex("(?:[^laeiouy]es|[^laeiouy]ed|[^laeiouy]e)$")] private static partial Regex SilentEnd();
    [GeneratedRegex("^y")] private static partial Regex LeadingY();
    [GeneratedRegex("[aeiouy]{1,2}")] private static partial Regex VowelGroup();
}

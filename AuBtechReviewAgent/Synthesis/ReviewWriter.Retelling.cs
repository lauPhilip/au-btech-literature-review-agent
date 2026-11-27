using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// A finding told again in other words (issue #744). In run 866a897b section 4.4 no longer repeated the figures of 4.3,
/// but it told the same findings again ("measurable gains in security compliance, defect reduction, productivity" after
/// 4.3's "productivity gains, defect reduction, and security compliance gains"), and a sentence of 4.1 summed up the
/// three sentences before it. The check compares the words a citation is attached to with the words of the earlier
/// sentences that cite the same source, so a sentence that cites several sources is judged one source at a time.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>A part of a sentence retells a finding when it has at least this many words in common with the earlier sentences...</summary>
    public const int RetoldMinShared = 3;

    /// <summary>...and those shared words are at least this share of its own words.</summary>
    public const double RetoldShare = 0.6;

    private static readonly HashSet<string> RetellIgnoredWords = new(StringComparer.Ordinal)
    {
        "this", "that", "these", "those", "which", "with", "from", "were", "have", "been", "their", "they", "also", "more",
        "most", "such", "into", "than", "only", "both", "each", "some", "report", "reported", "study", "show", "shown",
        "find", "found", "suggest", "suggested", "while", "whereas", "where", "when", "other", "another",
    };

    /// <summary>The words of four letters or more, lowered, with a plural "s" taken off, and common words left out.</summary>
    public static HashSet<string> FindingWords(string text) =>
        Regex.Matches(Regex.Replace(text, @"\[[^\]]*\]", " "), @"\p{L}{4,}")
            .Select(m => m.Value.ToLowerInvariant())
            .Select(w => w.Length > 4 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w)
            .Where(w => !RetellIgnoredWords.Contains(w)).ToHashSet();

    /// <summary>Where one clause of a sentence ends and the next begins.</summary>
    private static readonly Regex CitedClauseBreak = new(@";|,\s+(?:while|whereas|but|and)\b", RegexOptions.Compiled);

    /// <summary>
    /// Each source a sentence cites, with the words its citation is attached to (issue #744). A marker that opens its
    /// clause ("[4] identifies X") takes the words after it, up to the next marker or clause break; any other marker takes
    /// the words before it, back to the previous marker or clause break. Public for unit tests.
    /// </summary>
    public static IReadOnlyList<(int Reference, HashSet<string> Words)> CitedParts(string sentence)
    {
        var markers = Regex.Matches(sentence, @"\[[^\]]*\]").Where(m => CitationSupportChecker.ReferencesIn(m.Value).Count > 0).ToList();
        var parts = new List<(int, HashSet<string>)>();
        for (int i = 0; i < markers.Count; i++)
        {
            int end = markers[i].Index + markers[i].Length;
            string part;
            if (Regex.IsMatch(sentence[end..], @"^\s*\p{L}"))
            {
                part = sentence[end..(i + 1 < markers.Count ? markers[i + 1].Index : sentence.Length)];
                if (CitedClauseBreak.Match(part) is { Success: true } first) part = part[..first.Index];
            }
            else
            {
                part = sentence[(i == 0 ? 0 : markers[i - 1].Index + markers[i - 1].Length)..markers[i].Index];
                if (CitedClauseBreak.Matches(part) is { Count: > 0 } breaks) part = part[(breaks[^1].Index + breaks[^1].Length)..];
            }
            var words = FindingWords(part);
            foreach (int r in CitationSupportChecker.ReferencesIn(markers[i].Value)) parts.Add((r, words));
        }
        return parts;
    }

    /// <summary>
    /// The sources whose finding a sentence tells again: the part of the sentence cited to the source shares at least
    /// <see cref="RetoldMinShared"/> words, and <see cref="RetoldShare"/> of its own words, with the earlier sentences
    /// that cite the same source. Public for unit tests.
    /// </summary>
    public static IReadOnlyList<int> RetoldSources(string sentence, IEnumerable<(string Sentence, IReadOnlyList<int> Refs)> before)
    {
        var earlier = before.ToList();
        var retold = new List<int>();
        foreach (var (reference, words) in CitedParts(sentence))
        {
            if (words.Count < RetoldMinShared || retold.Contains(reference)) continue;
            var said = earlier.Where(e => e.Refs.Contains(reference)).SelectMany(e => FindingWords(e.Sentence)).ToHashSet();
            int shared = words.Count(said.Contains);
            if (shared >= RetoldMinShared && shared >= words.Count * RetoldShare) retold.Add(reference);
        }
        return retold;
    }
}

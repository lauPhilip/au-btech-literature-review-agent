using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The last line of the rule on references per sentence for the discussion and the closing (issue #742). Run 866a897b lost
/// its discussion and conclusion because every try had one sentence over the rule: "Workflow-level safeguards confine unsafe
/// behavior [2, 12, 9], and orchestration-based redundancy addresses fault tolerance [16]." The rule is a soft check there,
/// and a sentence still over it on the last try is split in code at its clauses, so the reader never loses a section to a
/// citation count.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>
    /// A clause boundary right after a marker: "; " or ", and", ", but", ", while", ", whereas" (with or without the comma).
    /// The connective is kept so a contrast can be said again in the new sentence.
    /// </summary>
    private static readonly Regex ClauseAfterMarker = new(
        @"(?<=\])(?:[ \t]*;[ \t]+|,?[ \t]+(?<link>and|but|while|whereas)[ \t]+)", RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="text"/> with every sentence that cites more than <see cref="MaxReferencesPerSentence"/> references
    /// split at its clauses into sentences that each keep to the rule. A clause that opens with "but", "while" or "whereas"
    /// becomes a sentence that opens with "By contrast,". Nothing else changes: no marker is dropped, moved or added. A
    /// sentence that cannot be split that way (one clause cites too many, or a part has no words of its own, as in a list of
    /// markers) is left as it is. Each split and each sentence left over the rule is written to <paramref name="notes"/>,
    /// for the run's files. Public for unit tests.
    /// </summary>
    public static string SplitOverCited(string? text, string part, List<string> notes)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = new StringBuilder(text.Length);
        int copied = 0;
        foreach (var (sentence, references) in OverCitedSentences(text))
        {
            int at = text.IndexOf(sentence, copied, StringComparison.Ordinal);
            if (at < 0) continue;
            if (SplitAtClauses(sentence) is { } split)
            {
                result.Append(text, copied, at - copied).Append(split);
                copied = at + sentence.Length;
                notes.Add($"In {part}, a sentence that cited {references.Count} references was split in code at its clauses: \"{sentence}\" became \"{split}\"");
            }
            else
                notes.Add($"In {part}, a sentence still cites {references.Count} references after every try, and it could not be split at its clauses, so it was kept: \"{sentence}\"");
        }
        return result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The sentence split at its clauses so that each new sentence cites at most the rule's number, or null.</summary>
    private static string? SplitAtClauses(string sentence)
    {
        var cuts = ClauseAfterMarker.Matches(sentence).Cast<Match>().ToList();
        if (cuts.Count == 0) return null;
        var parts = new List<(string Text, string? Link)>();
        int start = 0;
        string? link = null;
        foreach (var cut in cuts)
        {
            parts.Add((sentence[start..cut.Index], link));
            link = cut.Groups["link"].Success ? cut.Groups["link"].Value.ToLowerInvariant() : null;
            start = cut.Index + cut.Length;
        }
        parts.Add((sentence[start..], link));

        // Join neighbouring clauses back together where the rule allows, so the split is as small as it can be.
        var merged = new List<(string Text, string? Link)> { parts[0] };
        for (int i = 1; i < parts.Count; i++)
        {
            var last = merged[^1];
            string joined = last.Text + Joiner(parts[i].Link) + parts[i].Text;
            if (CitationSupportChecker.ReferencesIn(joined).Count <= MaxReferencesPerSentence) merged[^1] = (joined, last.Link);
            else merged.Add(parts[i]);
        }
        if (merged.Count < 2) return null;
        if (merged.Any(p => CitationSupportChecker.ReferencesIn(p.Text).Count > MaxReferencesPerSentence || !HasOwnWords(p.Text))) return null;

        string end = Regex.Match(sentence.TrimEnd(), @"[.!?]$").Value;
        var sentences = merged.Select((p, i) =>
        {
            string body = p.Text.Trim();
            if (i < merged.Count - 1) body = body.TrimEnd(',', ' ') + ".";
            if (i == 0) return body;
            return p.Link is "but" or "while" or "whereas" ? "By contrast, " + body : Capitalised(body);
        }).ToList();
        string joinedSentences = string.Join(" ", sentences);
        return end.Length > 0 && !joinedSentences.EndsWith(end, StringComparison.Ordinal) ? joinedSentences + end : joinedSentences;
    }

    private static string Joiner(string? link) => link == null ? "; " : $", {link} ";

    /// <summary>Whether a clause has words besides its markers, so it can stand as a sentence.</summary>
    private static bool HasOwnWords(string clause) => Regex.Matches(Regex.Replace(clause, @"\[[^\]]*\]", ""), @"\p{L}{3,}").Count >= 2;

    private static string Capitalised(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}

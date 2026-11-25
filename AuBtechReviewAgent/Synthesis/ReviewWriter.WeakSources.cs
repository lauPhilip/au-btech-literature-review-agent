using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The rationale does not open on one weak source as established fact (issue #461). In the multivocal run 50e9a8e3 the
/// rationale's first sentence, "Most agent failures trace back to context failures ... [1]", rested on one GitHub README,
/// a 3rd-tier grey source. The front matter now gets the sources that are weak evidence on their own, with the reason
/// written in code (for the systematic review from the appraisal and the design, for the multivocal review from the
/// outlet tier), and a soft check sends back a rationale whose first sentence rests on one of them alone and states it
/// as fact rather than as that source's claim.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>The front-matter block that names the weak sources; empty when there are none, so the prompt is as before.</summary>
    public static string WeakSourcesBlock(IReadOnlyDictionary<int, string>? weak, WritingProfile profile) =>
        weak is not { Count: > 0 } ? ""
            : $"\n\nWEAK EVIDENCE ({profile.BaseFactsFrom}; each of these {profile.Sources} is weak evidence on its own):\n" +
              string.Join("\n", weak.OrderBy(w => w.Key).Select(w => $"[{w.Key}] {w.Value}"));

    /// <summary>The rationale's rule on weak sources, added to its requirement only when there are any.</summary>
    public static string WeakOpeningRule(IReadOnlyDictionary<int, string>? weak, WritingProfile profile) =>
        weak is not { Count: > 0 } ? ""
            : $" The first sentence does not rest on one {profile.Source} listed under weak evidence alone: open on what stronger {profile.Sources} say, cite it with others that state the same, or report it as that {profile.Source}'s own claim (\"one position paper argues ...\").";

    // Words that report a claim as a source's own, not as established fact.
    private static readonly Regex OwnClaim = new(@"\b(?:argu\w*|claim\w*|suggest\w*|propos\w*|contend\w*|assert\w*|posit\w*|according to|in the view of|believ\w*|maintain\w*|warn\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Why the rationale opens on one weak source as established fact, or null: its first sentence cites exactly one
    /// source, that source is in <paramref name="weak"/>, and the sentence does not report it as the source's own claim.
    /// Public for unit tests.
    /// </summary>
    public static string? WeakOpeningProblem(string? rationale, IReadOnlyDictionary<int, string>? weak, WritingProfile profile)
    {
        if (weak is not { Count: > 0 } || string.IsNullOrWhiteSpace(rationale)) return null;
        string first = CitationSupportChecker.SplitSentences(rationale).FirstOrDefault() ?? "";
        var cited = CitationSupportChecker.ReferencesIn(first);
        if (cited.Count != 1 || !weak.TryGetValue(cited[0], out var why) || OwnClaim.IsMatch(first)) return null;
        return $"the rationale opens on [{cited[0]}] alone as established fact, but {why}. Open on what stronger {profile.Sources} say, cite it with others that state the same, or report it as that {profile.Source}'s own claim (\"one ... argues\").";
    }
}

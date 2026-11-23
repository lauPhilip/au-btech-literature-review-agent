using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Checks the plan's selection criteria in code for signs that they are broad, and warns on the last planning step. It
/// never blocks a plan and never changes it. A live run with the old example's criteria (a list of subjects, then "with
/// practical experience, guidance, an evaluation or a tool", and a short exclusion) included 282 of 431 sources, and
/// every included source costs a page fetch, a quality call, an extraction and a second screening.
/// </summary>
public static class MultivocalCriteriaCheck
{
    /// <summary>The fewest separators (commas, semicolons, slashes and "or") for an inclusion criterion to read as a list.</summary>
    public const int ListSeparators = 5;

    /// <summary>The most words per alternative, on average, for an inclusion criterion to read as a list.</summary>
    public const double ListWordsPerItem = 5.0;

    /// <summary>Fewer words than this make an exclusion criterion very short.</summary>
    public const int ShortExclusionWords = 8;

    /// <summary>Phrases that let almost any source on the topic in.</summary>
    public static readonly IReadOnlyList<string> CatchAllPhrases = new[]
    {
        "any source", "any kind of", "anything", "or a tool", "practical experience", "or similar", "and so on", "etc",
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>The warnings for a plan's criteria, in plain English, in the order the form shows them; empty when none applies.</summary>
    public static IReadOnlyList<string> Warnings(MultivocalPlan plan) => Warnings(plan.InclusionCriteria, plan.ExclusionCriteria);

    public static IReadOnlyList<string> Warnings(string? inclusion, string? exclusion)
    {
        var warnings = new List<string>();
        string include = (inclusion ?? "").Trim(), exclude = (exclusion ?? "").Trim();
        if (include.Length > 0)
        {
            if (ReadsAsList(include))
                warnings.Add("The inclusion criterion is mostly a list of alternatives. A source that matches any one of them is included, so many sources may pass. Say what the main subject of a source must be.");
            var found = CatchAllPhrases.Where(p => Regex.IsMatch(include, $@"\b{Regex.Escape(p)}\b", RegexOptions.IgnoreCase, Timeout)).ToList();
            if (found.Count > 0)
                warnings.Add($"The inclusion criterion says {string.Join(" and ", found.Select(p => $"\"{p}\""))}. Words like these let almost any source on the topic in. Say what a source must contain instead.");
        }
        int excludeWords = Words(exclude);
        if (excludeWords == 0)
            warnings.Add("There is no exclusion criterion. Name the kinds of source to leave out, such as passing mentions, general tutorials or release notes.");
        else if (excludeWords < ShortExclusionWords)
            warnings.Add("The exclusion criterion is very short. Name the kinds of source to leave out, such as passing mentions, general tutorials or release notes.");
        return warnings;
    }

    /// <summary>Whether a criterion is mostly separators: many of them, with few words between each.</summary>
    public static bool ReadsAsList(string text)
    {
        int separators = Regex.Matches(text, @"[,;/]|\bor\b", RegexOptions.IgnoreCase, Timeout).Count;
        return separators >= ListSeparators && (double)Words(text) / (separators + 1) <= ListWordsPerItem;
    }

    private static int Words(string text) => Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}'’-]*", RegexOptions.None, Timeout).Count;
}

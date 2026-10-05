using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// Why a record was excluded at screening, in a few fixed groups, so the funnel and the report can say "12 off
/// topic, 5 matched an exclusion criterion" instead of one number (PRISMA 2020 item 16a asks for the reasons). The
/// model picks the group when it excludes a record; the peer-review filter and the human reviewer set their own.
/// The model's free-text reasoning stays in the ledger for every record.
/// </summary>
public static class ExclusionReasons
{
    public const string OffTopic = "off-topic";
    public const string InclusionNotMet = "inclusion-not-met";
    public const string ExclusionMatched = "exclusion-matched";
    public const string TooLittleInformation = "too-little-information";
    public const string NotPeerReviewed = "not-peer-reviewed";
    public const string ByReviewer = "reviewer-decision";
    public const string Unspecified = "unspecified";

    /// <summary>The groups the screening model may choose from.</summary>
    public static readonly IReadOnlyList<string> ModelKeys = new[] { OffTopic, InclusionNotMet, ExclusionMatched, TooLittleInformation };

    public static string Label(string? key) => key switch
    {
        OffTopic => "Off topic",
        InclusionNotMet => "Inclusion criteria not met",
        ExclusionMatched => "Matches an exclusion criterion",
        TooLittleInformation => "Too little information to include",
        NotPeerReviewed => "Not peer reviewed",
        ByReviewer => "Excluded by the reviewer",
        _ => "Reason not recorded",
    };

    public sealed record Group(string Key, string Label, int Count);

    /// <summary>The excluded records grouped by reason, largest group first (ties in the order of the keys above).</summary>
    public static List<Group> Count(IEnumerable<ScreeningLog> screening)
    {
        var order = ModelKeys.Concat(new[] { NotPeerReviewed, ByReviewer, Unspecified }).ToList();
        return screening
            .Where(l => l.Decision == "Excluded")
            .GroupBy(l => string.IsNullOrWhiteSpace(l.ExclusionReason) ? Unspecified : l.ExclusionReason!)
            .Select(g => new Group(g.Key, Label(g.Key), g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => order.IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i)
            .ToList();
    }

    /// <summary>One sentence for the report, or null when nothing was excluded.</summary>
    public static string? Sentence(IEnumerable<ScreeningLog> screening)
    {
        var groups = Count(screening);
        if (groups.All(g => g.Key == Unspecified)) return null; // a run from before reasons were recorded
        int total = groups.Sum(g => g.Count);
        return $"Reasons for exclusion at screening ({total} record{(total == 1 ? "" : "s")}): " +
               string.Join("; ", groups.Select(g => $"{g.Label.ToLowerInvariant()} ({g.Count})")) + ".";
    }
}

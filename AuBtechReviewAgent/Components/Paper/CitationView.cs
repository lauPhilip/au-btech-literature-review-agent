using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// How a citation's verdict is shown on a paper page, the same for every kind of review: its label, its colour, the
/// id of its number on the page, and the citations that need a reader's attention, in reading order.
/// </summary>
public static class CitationView
{
    public static string VerdictLabel(string? verdict) => verdict switch
    {
        CitationSupportChecker.Supported => "Supported",
        CitationSupportChecker.Partial => "Partly supported",
        CitationSupportChecker.NotSupported => "Not supported",
        CitationSupportChecker.Unverifiable => "Could not be verified",
        CitationSupportChecker.InsufficientEvidence => "Not enough text to check (abstract only)",
        _ => "Not checked",
    };

    public static string VerdictText(string? verdict) => verdict switch
    {
        CitationSupportChecker.Supported => "text-green-700",
        CitationSupportChecker.Partial => "text-amber-700",
        CitationSupportChecker.NotSupported => "text-red-700",
        CitationSupportChecker.InsufficientEvidence => "text-sky-700",
        _ => "text-gray-500",
    };

    /// <summary>The id of a citation number on the page.</summary>
    public static string CiteId(string field, int sentence, int reference) => $"cite-{field}-{sentence}-{reference}";

    /// <summary>
    /// Citations that need a reader's attention (partly or not supported), in the order they appear in the paper;
    /// <paramref name="rank"/> gives each field's place.
    /// </summary>
    public static List<CitationSupportResult> Attention(IEnumerable<CitationSupportResult> checks, Func<string, int> rank) => checks
        .Where(c => c.Verdict is CitationSupportChecker.Partial or CitationSupportChecker.NotSupported)
        .OrderBy(c => rank(c.Field)).ThenBy(c => c.SentenceIndex).ThenBy(c => c.Reference).ToList();

    /// <summary>
    /// A field's place in the reading order of a paper: the introduction, the results overview, the theme sections in
    /// order, the artifact, the discussion, and anything else after it.
    /// </summary>
    public static int FieldRank(string field, IReadOnlyList<string> themeFields)
    {
        if (field == "rationaleItem") return -1;
        if (field == "synthesisResultsItem") return 0;
        int section = themeFields.ToList().IndexOf(field);
        if (section >= 0) return 1 + section;
        if (field.StartsWith("artifact-", StringComparison.Ordinal)) return 1000;
        return field == "discussionItem" ? 2000 : 3000;
    }
}

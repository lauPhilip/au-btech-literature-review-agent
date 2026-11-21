using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>One line of a report's contents list: the id of the section on the page, its label and its level (1 or 2).</summary>
public sealed record OutlineEntry(string Id, string Label, int Level);

/// <summary>
/// The sections of a systematic review's report page, in the order the page shows them, for its contents list
/// (<c>Components/Paper/ReportContents.razor</c>), and the filters of its citation-check panel. The page gives each
/// section the id listed here, so the list and the page cannot drift apart.
/// </summary>
public static class ReportOutline
{
    public const string AbstractId = "abstract";
    public const string IntroductionId = "s1";
    public const string MethodologyId = "s2";
    public const string StudiesId = "s3";
    public const string ResultsId = "s4";
    public const string DiscussionId = "s5";
    public const string ConclusionId = "s6";
    public const string DeclarationsId = "declarations";
    public const string ReferencesId = "references";
    public const string AppendixId = "appendix";

    /// <summary>The id of theme subsection <paramref name="index"/> (0-based), numbered 4.2, 4.3, … on the page.</summary>
    public static string ThemeId(int index) => $"s4-{index + 2}";

    /// <summary>Declarations are section 7 when the report has a conclusion, and section 6 when it has none (older runs).</summary>
    public static int DeclarationsNumber(PrismaReport report) => HasConclusion(report) ? 7 : 6;

    public static bool HasConclusion(PrismaReport report) => !string.IsNullOrWhiteSpace(report.ConclusionItem);

    /// <summary>The contents of a systematic review's report, with each theme of the synthesis under section 4.</summary>
    public static List<OutlineEntry> Systematic(PrismaReport report)
    {
        var list = new List<OutlineEntry>
        {
            new(AbstractId, "Abstract", 1),
            new(IntroductionId, "1 Introduction", 1),
            new(MethodologyId, "2 Methodology", 1),
            new(StudiesId, "3 Included studies", 1),
            new(ResultsId, "4 Results and synthesis", 1),
        };
        for (int k = 0; k < report.SynthesisSections.Count; k++)
        {
            string heading = report.SynthesisSections[k].Heading?.Trim() ?? "";
            list.Add(new(ThemeId(k), $"4.{k + 2} {(heading.Length == 0 ? "Theme " + (k + 1) : heading)}", 2));
        }
        list.Add(new(DiscussionId, "5 Discussion", 1));
        if (HasConclusion(report)) list.Add(new(ConclusionId, "6 Conclusion", 1));
        list.Add(new(DeclarationsId, $"{DeclarationsNumber(report)} Declarations", 1));
        list.Add(new(ReferencesId, "References", 1));
        list.Add(new(AppendixId, "Appendix", 1));
        return list;
    }

    // ── The citation-check panel's filters ──────────────────────────────────────────────────────

    public const string All = "all";
    public const string Flagged = "flagged";
    public const string AbstractOnly = "abstract";

    /// <summary>The three filters, in the order the panel shows them, with their labels.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Filters = new[]
    {
        (All, "All"),
        (Flagged, "Flagged"),
        (AbstractOnly, "Abstract only"),
    };

    /// <summary>Whether a citation belongs to a filter: flagged is partly or not supported; abstract only is the
    /// verdict "not enough text to check" or any check that could see only the abstract.</summary>
    public static bool Matches(CitationSupportResult check, string filter) => filter switch
    {
        Flagged => check.Verdict is CitationSupportChecker.Partial or CitationSupportChecker.NotSupported,
        AbstractOnly => check.Verdict == CitationSupportChecker.InsufficientEvidence
            || string.Equals(check.EvidenceBasis, "abstract only", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    /// <summary>The citations of a filter in reading order; <paramref name="rank"/> gives each field's place (<see cref="CitationView.FieldRank"/>).</summary>
    public static List<CitationSupportResult> InOrder(IEnumerable<CitationSupportResult> checks, string filter, Func<string, int> rank) => checks
        .Where(c => Matches(c, filter))
        .OrderBy(c => rank(c.Field)).ThenBy(c => c.SentenceIndex).ThenBy(c => c.Reference).ToList();

    /// <summary>What the step button names: "Next flagged citation", "Next citation" or "Next abstract-only citation".</summary>
    public static string NextLabel(string filter) => filter switch
    {
        Flagged => "Next flagged citation",
        AbstractOnly => "Next abstract-only citation",
        _ => "Next citation",
    };
}

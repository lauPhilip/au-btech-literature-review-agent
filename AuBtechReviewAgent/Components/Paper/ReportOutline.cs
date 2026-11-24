using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>One line of a report's contents list: the id of the section on the page, its label and its level (1 or 2).</summary>
public sealed record OutlineEntry(string Id, string Label, int Level);

/// <summary>
/// The sections of a review's report page (the systematic review's, and the grey literature review's paper), in the order the page shows them, for its contents list
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

    /// <summary>The id of result subsection <paramref name="index"/> (0-based) of a multivocal paper, numbered 4.1, 4.2, … on the page.</summary>
    public static string ResultId(int index) => $"s4-{index + 1}";

    /// <summary>The summary for practitioners is section 6 of a multivocal paper when it has one.</summary>
    public const string SummaryId = "s6";

    /// <summary>The conclusion of a multivocal paper, after its summary for practitioners (issue #461).</summary>
    public const string MultivocalConclusionId = "s7";

    /// <summary>The number of a multivocal paper's conclusion: 7 after a summary for practitioners, 6 without one.</summary>
    public static int ConclusionNumber(MultivocalPaperFile paper) => paper.SummaryItems.Count > 0 ? 7 : 6;

    /// <summary>The number of a multivocal paper's declarations: after the discussion, the summary and the conclusion it has.</summary>
    public static int DeclarationsNumber(MultivocalPaperFile paper) =>
        ConclusionNumber(paper) + (string.IsNullOrWhiteSpace(paper.Conclusion) ? 0 : 1);

    /// <summary>The heading of a multivocal result: "RQ1: heading" for a written theme, the heading alone otherwise.</summary>
    public static string ResultHeading(PaperResult result) =>
        result.Kind == "theme" && !string.IsNullOrWhiteSpace(result.Question) ? $"{result.Question}: {result.Heading}" : result.Heading;

    /// <summary>The contents of a multivocal paper (the grey literature review's paper page), with each result under section 4.</summary>
    public static List<OutlineEntry> Multivocal(MultivocalPaperFile paper)
    {
        var list = new List<OutlineEntry>
        {
            new(AbstractId, "Abstract", 1),
            new(IntroductionId, "1 Introduction", 1),
            new(MethodologyId, "2 Method", 1),
            new(StudiesId, "3 Data and the flow of sources", 1),
            new(ResultsId, "4 Results", 1),
        };
        for (int k = 0; k < paper.Results.Count; k++)
        {
            string heading = ResultHeading(paper.Results[k]).Trim();
            list.Add(new(ResultId(k), $"4.{k + 1} {(heading.Length == 0 ? "Result " + (k + 1) : heading)}", 2));
        }
        list.Add(new(DiscussionId, "5 Discussion", 1));
        if (paper.SummaryItems.Count > 0) list.Add(new(SummaryId, "6 Summary for practitioners", 1));
        if (!string.IsNullOrWhiteSpace(paper.Conclusion)) list.Add(new(MultivocalConclusionId, $"{ConclusionNumber(paper)} Conclusion", 1));
        list.Add(new(DeclarationsId, $"{DeclarationsNumber(paper)} Declarations", 1));
        list.Add(new(ReferencesId, "References", 1));
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

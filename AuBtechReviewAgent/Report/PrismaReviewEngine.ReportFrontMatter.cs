using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The front matter, artifact and closing sections of a systematic report, and the facts about the run they may use.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>What the front matter may say about the run, counted in code from the ledger.</summary>
    private static string FrontMatterFacts(ReviewState state, ThematicCodebook? book, string includedFacts, string screeningFacts)
    {
        var s = state.Stats;
        var lines = new List<string>
        {
            $"Records identified: {s.TotalIdentified}; duplicates removed: {s.DuplicatesRemoved}{OtherRemovals(state)}; records screened: {s.Screened}; included: {s.Included}; excluded: {s.Excluded}.",
            $"Included set: {includedFacts}",
            $"Screening: {screeningFacts}",
            $"Full text was read for {s.FullTextRetrieved} of the {s.Included} included studies; the others were read from their abstracts.",
        };
        if (book != null) lines.Add($"Thematic synthesis: {book.Themes.Count} theme{(book.Themes.Count == 1 ? "" : "s")} from {book.Codes.Count} code{(book.Codes.Count == 1 ? "" : "s")}.");
        if (RemovalsBeforeScreening(s).Any(r => r.Label != "duplicates"))
            lines.Add("Wherever both the records identified and the records screened are given, every removal between them is given too, as listed above.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The facts about the evidence base for the front matter (issue #146): the introduction names the design of the
    /// studies it cites, so it gets each study's design and what it was read from, as the discussion does.
    /// </summary>
    private static string EvidenceBaseFacts(ReviewEvidence? evidence) =>
        string.IsNullOrWhiteSpace(evidence?.BaseFacts) ? ""
            : $"\nEvidence base (from the data extraction; name the design of a cited study only as given here): {evidence.BaseFacts}";

    /// <summary>The records removed before screening for a reason other than being a duplicate, as the flow diagram counts them (issue #118).</summary>
    private static string OtherRemovals(ReviewState state)
    {
        var s = state.Stats;
        string range = state.YearFrom > 0 && state.YearTo > 0 ? $" ({state.YearFrom} to {state.YearTo})" : "";
        var parts = new List<string>();
        if (s.OutsideDateRange > 0) parts.Add($"removed because their publication year was outside the year range{range}: {s.OutsideDateRange}");
        if (s.CappedBeyondMaxResults > 0) parts.Add($"removed over the per-source cap: {s.CappedBeyondMaxResults}");
        if (s.ScreeningErrors > 0) parts.Add($"could not be screened: {s.ScreeningErrors}");
        return string.Concat(parts.Select(p => "; " + p));
    }

    /// <summary>Every count of records removed between identification and screening that is above zero, as the flow diagram has them.</summary>
    private static List<(string Label, int Count)> RemovalsBeforeScreening(ReviewStats s) =>
        new List<(string Label, int Count)>
        {
            ("duplicates", s.DuplicatesRemoved),
            ("the records outside the year range", s.OutsideDateRange),
            ("the records over the per-source cap", s.CappedBeyondMaxResults),
            ("the records that could not be screened", s.ScreeningErrors),
        }.Where(r => r.Count > 0).ToList();

    /// <summary>
    /// Null when the abstract's record counts add up; otherwise what it leaves out. An abstract that gives both the
    /// records identified and the records screened must give every removal between them too, so a reader can follow
    /// the numbers to the flow diagram (issue #118). An abstract that gives neither, or only one, is left alone.
    /// </summary>
    public static string? AbstractCountsProblem(string abstractText, ReviewStats s)
    {
        if (s.TotalIdentified == s.Screened) return null;
        var numbers = ReviewWriter.NumbersIn(abstractText);
        if (!numbers.Contains(s.TotalIdentified.ToString()) || !numbers.Contains(s.Screened.ToString())) return null;
        var missing = RemovalsBeforeScreening(s).Where(r => !numbers.Contains(r.Count.ToString())).ToList();
        return missing.Count == 0 ? null
            : $"the abstract gives the {s.TotalIdentified} records identified and the {s.Screened} screened but leaves out " +
              $"{string.Join(" and ", missing.Select(r => $"{r.Label} ({r.Count})"))}; give every removal between them, as the facts list them.";
    }

    /// <summary>
    /// Keeps the title a title: first line only, no trailing full stop, and if the model returned a
    /// paragraph (or nothing) fall back to a neutral title built from the query.
    /// </summary>
    public static string CleanTitle(string rawTitle, string query)
    {
        string t = (rawTitle ?? "").Split('\n')[0].Trim().TrimEnd('.');
        int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool looksLikeParagraph = Regex.Matches(t, @"[.!?]\s+[A-Z]").Count > 0 || words > 30;
        if (string.IsNullOrWhiteSpace(t) || t == "Extraction failed" || looksLikeParagraph)
            return $"{query.Trim()}: A Systematic Literature Review";
        return t;
    }
}

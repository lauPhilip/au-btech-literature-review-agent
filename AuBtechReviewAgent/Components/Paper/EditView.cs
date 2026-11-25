using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// What the checked editor on a report page shows, worked out from the run's edits (report-edits.json): which texts
/// the reader may edit, how many edits are used, which sentences carry an "edited" mark, which edits can be undone,
/// and the names of the parts of the report in plain words.
/// </summary>
public static class EditView
{
    /// <summary>
    /// True when the reader may edit this text: the browser holds the run's edit key, has the editor switched on, and
    /// the text is prose the model wrote (<see cref="ReportEdits.IsEditable"/>). Parts written in code never are.
    /// </summary>
    public static bool CanEdit(bool owner, bool editing, string field) => owner && editing && ReportEdits.IsEditable(field);

    /// <summary>How many of the run's edits are used; an undo does not count.</summary>
    public static int Used(ReportEditLog log) => log.Edits.Count(e => e.Action == ReportEdit.Edited);

    /// <summary>"3 of 100 edits used".</summary>
    public static string UsedLine(ReportEditLog log) =>
        string.Create(CultureInfo.InvariantCulture, $"{Used(log)} of {PrismaReviewEngine.MaxReportEditsPerRun} edits used");

    /// <summary>The sentences of one text that read differently from the run's own text now, with the edit in force.</summary>
    public static Dictionary<int, int> EditedIn(ReportEditLog log, string field) => log.Current()
        .Where(c => c.Key.Field == field)
        .ToDictionary(c => c.Key.SentenceIndex, c => c.Value.Number);

    /// <summary>The edits that can be undone: the newest edit in force of each sentence.</summary>
    public static HashSet<int> Undoable(ReportEditLog log) => log.Current().Values.Select(e => e.Number).ToHashSet();

    /// <summary>The sentence as the report reads now, or null when the report has no such sentence.</summary>
    public static string? SentenceOf(PrismaReport report, string field, int index)
    {
        string? text = ReportEdits.FieldText(report, field);
        if (text == null) return null;
        var spans = ReportEdits.SentenceSpans(text);
        return index >= 0 && index < spans.Count ? text.Substring(spans[index].Start, spans[index].Length) : null;
    }

    /// <summary>The name of a part of the report in plain words, for labels and the history of edits.</summary>
    public static string FieldLabel(PrismaReport report, string field)
    {
        if (field.StartsWith("synthesis-", StringComparison.Ordinal))
        {
            var section = report.SynthesisSections?.FirstOrDefault(s => s.Field == field);
            return section == null || string.IsNullOrWhiteSpace(section.Heading) ? "a theme of the results" : $"the theme \"{section.Heading.Trim()}\"";
        }
        if (field.StartsWith("researchAnswer", StringComparison.Ordinal))
            return $"the answer to research question {field["researchAnswer".Length..]}";
        return field switch
        {
            "rationaleItem" => "the introduction",
            "synthesisResultsItem" => "the overview of the results",
            "discussionItem" => "the discussion",
            "evidenceLimitationsItem" => "the limitations of the evidence",
            "implicationsItem" => "the implications",
            "futureResearchItem" => "future research",
            "conclusionItem" => "the conclusion",
            _ => "the report",
        };
    }

    /// <summary>"2 supported, 1 partly supported": the verdicts of one edit, for the history.</summary>
    public static string Summary(IReadOnlyCollection<CitationSupportResult> checks)
    {
        if (checks.Count == 0) return "No citation to check";
        return string.Join(", ", checks.GroupBy(c => CitationView.VerdictLabel(c.Verdict).ToLowerInvariant())
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {g.Key}")));
    }
}

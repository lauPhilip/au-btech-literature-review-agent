using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Checks 4, 7 and 8: how many references a sentence cites, how the citation check came out, and whether the reference
/// list and the citations agree. Run 50e9a8e3 had nineteen markers with ten or more sources; a reader cannot tell which
/// source says what in such a sentence.
/// </summary>
public static partial class PaperSelfCheck
{
    private static string Percent(int part, int whole) => whole == 0 ? "0%" : (100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Check 4: no written sentence cites more than <see cref="ReviewWriter.MaxReferencesPerSentence"/> references, and the share that cites one or two.</summary>
    internal static PaperCheck CitationLoadCheck(PaperCheckInput input)
    {
        int max = ReviewWriter.MaxReferencesPerSentence;
        var check = new PaperCheck
        {
            Id = "citation-load",
            Title = $"At most {max} references per sentence",
            Explanation = $"A sentence that cites many {input.Sources} at once does not tell the reader which one says what, and the citation check cannot judge it. " +
                $"A sentence cites at most {max}, ideally one or two. Lists written in code, such as the note on {input.Sources} not cited, are left out.",
        };
        int cited = 0, light = 0;
        foreach (var part in input.Written)
            foreach (var sentence in CitationSupportChecker.SplitSentences(part.Text))
            {
                var refs = CitationSupportChecker.ReferencesIn(sentence);
                if (refs.Count == 0) continue;
                cited++;
                if (refs.Count <= 2) light++;
                if (refs.Count > max)
                    check.Evidence.Add(new(part.Name, part.Field, Short(sentence), $"Cites {refs.Count} references."));
            }
        check.Outcome = check.Evidence.Count == 0 ? CheckOutcome.Pass : CheckOutcome.Fault;
        check.Summary = cited == 0 ? "No sentence carries a citation."
            : (check.Evidence.Count == 0 ? $"No sentence cites more than {max}" : $"{Plural(check.Evidence.Count, "sentence cites", "sentences cite")} more than {max}") +
              $"; {Percent(light, cited)} of the {cited} cited sentences cite one or two.";
        return check;
    }

    /// <summary>Check 7: the outcome of the citation check after repair, and how many citations it removed.</summary>
    internal static PaperCheck CitationCheckCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "citation-check",
            Title = "Citations backed by their sources",
            Explanation = $"Every citation was checked against the text of the {input.Source} it cites, and those that failed were repaired once and checked again. " +
                "A citation still judged not supported stays in the paper, marked in red; a partly supported one is marked too. Removed citations were still not supported after the repair.",
        };
        var s = input.CitationsFinal;
        var shown = ShownMismatches(input);
        if (s == null || s.Checked == 0)
        {
            bool anyCited = input.Written.Any(p => ThematicSynthesis.CitedIn(p.Text).Count > 0);
            check.Evidence.AddRange(shown);
            check.Outcome = anyCited || shown.Count > 0 ? CheckOutcome.Fault : CheckOutcome.Pass;
            check.Summary = anyCited ? "The paper cites sources, but no citation was checked."
                : shown.Count > 0 ? "The run page gives citation counts the report does not have." : "The paper cites no sources, so there was nothing to check.";
            return check;
        }
        if (s.NotSupported > 0)
            check.Evidence.Add(new("Citation check", "", $"{s.NotSupported} not supported", "Still not supported after the repair; marked in red in the paper."));
        if (s.PartiallySupported > 0)
            check.Evidence.Add(new("Citation check", "", $"{s.PartiallySupported} partly supported", "The source supports part of the sentence; marked in the paper."));
        if (s.InsufficientEvidence + s.Unverifiable > 0)
            check.Evidence.Add(new("Citation check", "", $"{s.InsufficientEvidence + s.Unverifiable} not checkable", "The source's text did not let the check judge the claim (abstract only or no text)."));
        if (input.CitationsRemoved > 0)
            check.Evidence.Add(new("Citation check", "", $"{input.CitationsRemoved} removed", "Still not supported after the repair, so taken out of the paper."));
        if (input.CitationsInitial is { } before)
            check.Evidence.Add(new("Citation check", "", $"Before repair: {before.NotSupported} not supported, {before.PartiallySupported} partly", $"Of {before.Checked} citations first checked."));
        check.Evidence.AddRange(shown);
        check.Outcome = s.NotSupported > 0 || shown.Count > 0 ? CheckOutcome.Fault
            : s.PartiallySupported + s.InsufficientEvidence + s.Unverifiable + input.CitationsRemoved > 0 ? CheckOutcome.Note : CheckOutcome.Pass;
        check.Summary = $"{s.Checked} checked: {Percent(s.Supported, s.Checked)} supported, {Percent(s.PartiallySupported, s.Checked)} partly, {Percent(s.NotSupported, s.Checked)} not supported" +
            (input.CitationsRemoved > 0 ? $"; {input.CitationsRemoved} removed" : "") +
            (shown.Count > 0 ? "; the run page gives other counts." : ".");
        return check;
    }

    /// <summary>Each count of the citation check where the run page and the report differ; empty when they agree or the page has none.</summary>
    private static List<CheckEvidence> ShownMismatches(PaperCheckInput input)
    {
        var evidence = new List<CheckEvidence>();
        if (input.CitationsShown is not { } page) return evidence;
        var report = input.CitationsFinal ?? new CitationSupportSummary();
        foreach (var (what, onPage, inReport) in new[]
                 {
                     ("checked", page.Checked, report.Checked),
                     ("supported", page.Supported, report.Supported),
                     ("partly supported", page.PartiallySupported, report.PartiallySupported),
                     ("not supported", page.NotSupported, report.NotSupported),
                     ("removed", page.Removed, input.CitationsRemoved),
                 })
            if (onPage != inReport)
                evidence.Add(new("Run page", "", $"{onPage} {what} on the run page, {inReport} in the report",
                    "The run page and the report should give the same count of the citation check."));
        return evidence;
    }

    private static string Key(string? text) => Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    private static string DoiKey(string? doi) => Key(Regex.Replace(doi ?? "", @"^(?:https?://)?(?:dx\.)?doi\.org/", "", RegexOptions.IgnoreCase));

    /// <summary>Check 8: every cited number is in the reference list and every entry is cited, no work is listed twice, and what the entries lack.</summary>
    internal static PaperCheck ReferencesCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "references",
            Title = "References complete and consistent",
            Explanation = "Every [n] in the text should point to an entry in the reference list, and every entry should be cited. No work should be listed twice. " +
                "An entry without a year, a venue or a DOI is harder for a reader to find; the reference completion fills such gaps from Crossref, OpenAlex or arXiv where it can.",
        };
        var listed = input.References.Select(r => r.Number).ToHashSet();
        var cited = input.Parts.SelectMany(p => ThematicSynthesis.CitedIn(p.Text)).ToHashSet();
        var outcomes = new List<string>();
        var dangling = cited.Where(n => !listed.Contains(n)).OrderBy(n => n).ToList();
        if (dangling.Count > 0)
        {
            outcomes.Add(CheckOutcome.Fault);
            check.Evidence.Add(new("Text", "", RefList(dangling), "Cited in the text but not in the reference list."));
        }
        var unused = input.References.Where(r => !cited.Contains(r.Number)).Select(r => r.Number).ToList();
        if (unused.Count > 0)
        {
            outcomes.Add(CheckOutcome.Note);
            check.Evidence.Add(new("References", "", RefList(unused), "In the reference list but cited nowhere in the text."));
        }
        foreach (var group in input.References.Where(r => DoiKey(r.Doi).Length > 0).GroupBy(r => DoiKey(r.Doi))
                     .Concat(input.References.Where(r => Key(r.Title).Length > 20).GroupBy(r => Key(r.Title))).Where(g => g.Count() > 1))
        {
            var numbers = group.Select(r => r.Number).Distinct().OrderBy(n => n).ToList();
            if (numbers.Count < 2 || check.Evidence.Any(e => e.Text == RefList(numbers))) continue;
            outcomes.Add(CheckOutcome.Fault);
            check.Evidence.Add(new("References", "", RefList(numbers), "The same work is listed more than once."));
        }
        var lacking = new[]
        {
            ("year", input.References.Where(r => r.Year is null or <= 0).Select(r => r.Number).ToList()),
            ("venue", input.References.Where(r => string.IsNullOrWhiteSpace(r.Venue)).Select(r => r.Number).ToList()),
            ("DOI", input.References.Where(r => string.IsNullOrWhiteSpace(r.Doi)).Select(r => r.Number).ToList()),
        };
        foreach (var (what, numbers) in lacking.Where(l => l.Item2.Count > 0))
        {
            outcomes.Add(CheckOutcome.Note);
            check.Evidence.Add(new("References", "", RefList(numbers), $"No {what}."));
        }
        int completed = input.References.Count(r => r.FieldsCompleted > 0);
        check.Outcome = Worst(outcomes.ToArray());
        check.Summary = $"{Plural(input.References.Count, "entry", "entries")}" +
            (dangling.Count + unused.Count == 0 ? ", all cited and all in the list" : "") +
            (completed > 0 ? $"; the reference completion filled {Plural(input.References.Sum(r => r.FieldsCompleted), "field", "fields")} in {completed}" : "") +
            (lacking.Any(l => l.Item2.Count > 0) ? "; " + string.Join(", ", lacking.Where(l => l.Item2.Count > 0).Select(l => $"{l.Item2.Count} without a {l.Item1}")) : "") + ".";
        return check;
    }
}

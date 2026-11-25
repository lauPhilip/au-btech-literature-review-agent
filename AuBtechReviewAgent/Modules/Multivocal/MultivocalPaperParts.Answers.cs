using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

// The answers to questions about frequency or existence, written in code, and the note under each theme.
public static partial class MultivocalPaperParts
{
    // ---------- Results answered in code ----------

    /// <summary>
    /// The answer to a question about frequency or existence, in code: one table per attribute of the question, with
    /// each value, how many of the extracted sources state it, their share, and the sources by reference number.
    /// </summary>
    public static PaperSection CodeAnswer(MultivocalReportInput input, string number, PlannedQuestion question)
    {
        var tables = CodeAnswerTables(input, number);
        string lead = CodeAnswerLead(input, number, question);
        var latex = new StringBuilder();
        latex.AppendLine(Prose(lead)).AppendLine();
        foreach (var table in tables) latex.Append(ColumnTable(table));
        // Plain text with "\n" line ends on every system, so the lead can always be told from the tables.
        var text = new StringBuilder(lead);
        foreach (var table in tables)
        {
            text.Append("\n\n").Append(table.Caption);
            foreach (var row in table.Rows) text.Append('\n').Append(string.Join(" | ", row));
            if (!string.IsNullOrWhiteSpace(table.Note)) text.Append('\n').Append(table.Note);
        }
        return new PaperSection($"{number}: {question.Text}", text.ToString(), latex.ToString());
    }

    /// <summary>The sentence before the tables of a question answered in code.</summary>
    public static string CodeAnswerLead(MultivocalReportInput input, string number, PlannedQuestion question)
    {
        if (question.Type == MultivocalRelationship.Type) return MultivocalRelationship.Lead(input, number);
        var attributes = FixedMap(input).Attributes.Where(a => a.Serves(number)).ToList();
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        string kind = question.Type == "existence" ? "whether something exists" : "how often something occurs";
        if (attributes.Count == 0) return $"This question asks {kind}. The systematic map has no attribute for it, so it is not answered here.";
        string borrowed = attributes.All(a => a.Question != number)
            ? $" It counts the values of {string.Join(" and ", attributes.Select(a => $"\"{a.Name}\" ({a.Question})"))}, which the map records for {(attributes.Count == 1 ? "that question" : "those questions")}."
            : "";
        return $"This question asks {kind}, so it is answered by counting, in code, rather than in written text: the {(attributes.Count == 1 ? "table counts" : "tables count")} how many of the {extracted} extracted sources state each value of the systematic map, each value backed by a quote found in the source's kept page.{borrowed}{(input.Ledger.HasFormalPool ? " The grey and the academic sources are counted apart, each with its share of its own pool." : "")} A count says how many sources state a value, not how common it is in practice.";
    }

    /// <summary>
    /// The answer to a question about frequency or existence in a few sentences, in code, for the answers to the research
    /// questions (issue #461): per attribute of the question, the values most sources state, with their counts and shares.
    /// </summary>
    public static string CodeAnswerSummary(MultivocalReportInput input, string number)
    {
        if (IsRelationship(input, number)) return MultivocalRelationship.Summary(input, number);
        var attributes = FixedMap(input).Attributes.Where(a => a.Serves(number)).ToList();
        if (attributes.Count == 0) return "The systematic map has no attribute for this question, so the review does not answer it.";
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        var split = Split(input);
        var parts = attributes.Select(attribute =>
        {
            var counts = ValueCounts(input, attribute);
            var stated = counts.Where(c => c.Value != "not stated").ToList();
            int none = counts.FirstOrDefault(c => c.Value == "not stated").Sources;
            if (stated.Count == 0) return $"None of the {extracted} extracted sources states a value of \"{attribute.Name}\".";
            var top = stated.Take(TopValues).Select(c => $"{c.Value} ({CountPhrase(c.Sources, c.References, extracted, split)})").ToList();
            string list = top.Count == 1 ? top[0] : string.Join(", ", top.Take(top.Count - 1)) + " and " + top[^1];
            return $"Of the {extracted} extracted sources, {Stating(extracted - none)} a value of \"{attribute.Name}\" ({stated.Count} different {(stated.Count == 1 ? "value" : "values")}); the most often stated {(top.Count == 1 ? "is" : "are")} {list}.";
        });
        string pools = split == null ? "" : $" The shares are of each pool's own {split.Grey} grey and {split.Academic} academic sources.";
        return string.Join(" ", parts) + pools + " Counted in code; the table in the results gives every value with its sources.";

        static string Stating(int n) => n == 1 ? "1 states" : $"{n} state";
    }

    /// <summary>How many values the answer in code names per attribute.</summary>
    public const int TopValues = 3;

    private static string Percent(int part, int whole) =>
        whole == 0 ? "–" : $"{Math.Round(100.0 * part / whole).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>At most this many values named by one source each are listed as rows of an open attribute; the rest share one row.</summary>
    public const int SingleSourceRows = 8;

    /// <summary>
    /// The counts as shown: for an open attribute (names of tools, say) with more than <see cref="SingleSourceRows"/>
    /// values named by one source each, those values leave the rows and are returned apart, to be listed in one line.
    /// </summary>
    public static (IReadOnlyList<(string Value, int Sources, IReadOnlyList<int> References)> Shown, IReadOnlyList<(string Value, int Sources, IReadOnlyList<int> References)> NamedOnce) Compact(
        IReadOnlyList<(string Value, int Sources, IReadOnlyList<int> References)> counts, bool open)
    {
        var once = counts.Where(c => c.Sources == 1 && c.Value != "not stated").ToList();
        if (!open || once.Count <= SingleSourceRows) return (counts, Array.Empty<(string, int, IReadOnlyList<int>)>());
        return (counts.Where(c => !once.Contains(c)).ToList(), once);
    }

    public static IReadOnlyList<PaperTable> CodeAnswerTables(MultivocalReportInput input, string number)
    {
        if (IsRelationship(input, number)) return MultivocalRelationship.Tables(input, number);
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        var split = Split(input);
        string Cite(IEnumerable<int> refs) => refs.Any() ? $"[{string.Join(", ", refs.Distinct().OrderBy(n => n))}]" : "";
        return FixedMap(input).Attributes.Where(a => a.Serves(number)).Select(attribute =>
        {
            var counts = ValueCounts(input, attribute);
            var stating = counts.SelectMany(c => c.References).Distinct().ToList();
            var (shown, namedOnce) = Compact(counts, attribute.Open);
            // With a formal pool each value is counted in each pool, with its share of that pool (issue #459).
            var rows = shown.Select(c => split != null && c.Value == "not stated" && c.References.Count == 0
                ? NotStatedRow(c.Sources, stating, extracted, split)
                : CountRow(c.Value, c.Sources, c.References, extracted, split, Cite(c.References))).ToList();
            var notes = new List<string>();
            if (namedOnce.Count > 0)
            {
                var refs = namedOnce.SelectMany(c => c.References).Distinct().ToList();
                // Before "not stated", which stays the last row.
                int at = rows.FindIndex(r => r[0] == "not stated");
                rows.Insert(at < 0 ? rows.Count : at, CountRow($"{namedOnce.Count} more, each named by one source", refs.Count, refs, extracted, split, Cite(refs)));
                notes.Add($"Named by one source each: {string.Join("; ", namedOnce.Select(c => $"{c.Value} [{c.References[0]}]"))}.");
            }
            if (attribute.Multiple) notes.Add("A source can state several values, so the shares can add up to more than 100%.");
            if (split != null) notes.Add(SplitNote(split));
            return new PaperTable(attribute.Name, CountHeaders(split), rows, notes.Count == 0 ? null : string.Join(" ", notes));
        }).ToList();
    }

    // ---------- Notes under the themes ----------

    /// <summary>
    /// The note under a theme, in code: how many sources it rests on and which, their outlet tiers, their mean quality
    /// and the evidence label, then the tensions the synthesis found between its sources, each with the two sources.
    /// </summary>
    public static string ThemeNote(MultivocalReportInput input, SynthesisTheme theme)
    {
        var numbers = MultivocalReporter.SourceNumbers(input.Extraction);
        var byId = input.Synthesis.Findings.ToDictionary(f => f.Id);
        var cited = theme.Findings.Where(byId.ContainsKey).Select(id => byId[id].Address).Where(numbers.ContainsKey).Select(a => numbers[a]).Distinct().OrderBy(n => n).ToList();
        string Cite(IEnumerable<int> refs) => refs.Any() ? $" [{string.Join(", ", refs.Distinct().OrderBy(n => n))}]" : "";
        var sb = new StringBuilder();
        sb.Append($"What this theme rests on: {Sources(theme.Sources)}{Cite(cited)}{MultivocalFormalText.ThemePools(theme)}, {MultivocalFormalText.ThemeBasis(theme, input.Quality.MaxPoints)}.");
        foreach (var tension in theme.Tensions)
        {
            var refs = new[] { byId.GetValueOrDefault(tension.FindingA)?.Address, byId.GetValueOrDefault(tension.FindingB)?.Address }
                .OfType<string>().Where(numbers.ContainsKey).Select(a => numbers[a]);
            sb.Append($" Tension between sources{(tension.Between != null ? $", {tension.Between}" : "")}: {Sentence(tension.Description)}{Cite(refs)}".TrimEnd());
            if (!sb.ToString().EndsWith('.')) sb.Append('.');
        }
        return sb.ToString();
    }
}

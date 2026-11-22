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
        var attributes = FixedMap(input).Attributes.Where(a => a.Serves(number)).ToList();
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        string kind = question.Type == "existence" ? "whether something exists" : "how often something occurs";
        if (attributes.Count == 0) return $"This question asks {kind}. The systematic map has no attribute for it, so it is not answered here.";
        string borrowed = attributes.All(a => a.Question != number)
            ? $" It counts the values of {string.Join(" and ", attributes.Select(a => $"\"{a.Name}\" ({a.Question})"))}, which the map records for {(attributes.Count == 1 ? "that question" : "those questions")}."
            : "";
        return $"This question asks {kind}, so it is answered by counting, in code, rather than in written text: the {(attributes.Count == 1 ? "table counts" : "tables count")} how many of the {extracted} extracted sources state each value of the systematic map, each value backed by a quote found in the source's kept page.{borrowed} A count says how many sources state a value, not how common it is in practice.";
    }

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
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        string Share(int sources) => extracted == 0 ? "–" : $"{Math.Round(100.0 * sources / extracted).ToString(CultureInfo.InvariantCulture)}%";
        string Cite(IEnumerable<int> refs) => refs.Any() ? $"[{string.Join(", ", refs.Distinct().OrderBy(n => n))}]" : "";
        return FixedMap(input).Attributes.Where(a => a.Serves(number)).Select(attribute =>
        {
            var (shown, namedOnce) = Compact(ValueCounts(input, attribute), attribute.Open);
            var rows = shown.Select(c => (IReadOnlyList<string>)new[] { c.Value, c.Sources.ToString(CultureInfo.InvariantCulture), Share(c.Sources), Cite(c.References) }).ToList();
            var notes = new List<string>();
            if (namedOnce.Count > 0)
            {
                var refs = namedOnce.SelectMany(c => c.References).Distinct().ToList();
                // Before "not stated", which stays the last row.
                int at = rows.FindIndex(r => r[0] == "not stated");
                rows.Insert(at < 0 ? rows.Count : at, new[] { $"{namedOnce.Count} more, each named by one source", refs.Count.ToString(CultureInfo.InvariantCulture), Share(refs.Count), Cite(refs) });
                notes.Add($"Named by one source each: {string.Join("; ", namedOnce.Select(c => $"{c.Value} [{c.References[0]}]"))}.");
            }
            if (attribute.Multiple) notes.Add("A source can state several values, so the shares can add up to more than 100%.");
            return new PaperTable(attribute.Name, new[] { "Value", "Sources", "Share", "Cited" }, rows, notes.Count == 0 ? null : string.Join(" ", notes));
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
        sb.Append($"What this theme rests on: {Sources(theme.Sources)}{Cite(cited)}, {theme.FirstTier} from 1st-tier, {theme.SecondTier} from 2nd-tier and {theme.ThirdTier} from 3rd-tier outlets, ");
        sb.Append($"with a mean quality of {theme.MeanQuality.ToString("0.#", CultureInfo.InvariantCulture)} of {input.Quality.MaxPoints} points ({theme.EvidenceLabel}).");
        foreach (var tension in theme.Tensions)
        {
            var refs = new[] { byId.GetValueOrDefault(tension.FindingA)?.Address, byId.GetValueOrDefault(tension.FindingB)?.Address }
                .OfType<string>().Where(numbers.ContainsKey).Select(a => numbers[a]);
            sb.Append($" Tension between sources: {Sentence(tension.Description)}{Cite(refs)}".TrimEnd());
            if (!sb.ToString().EndsWith('.')) sb.Append('.');
        }
        return sb.ToString();
    }
}

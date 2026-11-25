using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

// The data section's figures and tables: the sources by kind and outlet tier, the quality scores and the map's counts.
public static partial class MultivocalPaperParts
{
    /// <summary>The extracted sources per kind of grey literature, with their outlet tier, most first.</summary>
    public static IReadOnlyList<(string Kind, string Label, int Tier, int Sources)> Kinds(MultivocalReportInput input)
    {
        var records = input.Ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record);
        return input.Extraction.Sources.Where(s => s.Error == null)
            .GroupBy(s => records.GetValueOrDefault(s.Address)?.Kind ?? "")
            // An academic source has no outlet tier (0): it is formal literature, drawn in its own colour.
            .Select(g => (Kind: g.Key, Label: g.Key == MultivocalSearcher.FormalKind ? "Academic literature"
                              : MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == g.Key).Label ?? (g.Key.Length > 0 ? g.Key : "Other"),
                          Tier: g.Key == MultivocalSearcher.FormalKind ? 0 : MultivocalSynthesiser.TierOf(g.Key), Sources: g.Count()))
            .OrderByDescending(x => x.Sources).ThenBy(x => x.Tier).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static string KindChart(MultivocalReportInput input)
    {
        var kinds = Kinds(input);
        if (kinds.Count == 0) return "No source was extracted.\n";
        var sb = new StringBuilder();
        int max = Math.Max(2, kinds.Max(k => k.Sources) + 1);
        // Plotted at y = 1..n with the kinds as tick labels; one plot per tier so each tier has its colour.
        string ticks = string.Join(",", Enumerable.Range(1, kinds.Count));
        string labels = string.Join(",", kinds.Select(k => "{" + PaperLatex.Escape(Short(k.Label)) + "}"));
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.8]");
        sb.AppendLine($"\\begin{{axis}}[xbar stacked, ytick={{{ticks}}}, yticklabels={{{labels}}}, y dir=reverse, xmin=0, xmax={max}, ymin=0.5, ymax={kinds.Count + 0.5}, xlabel={{Sources}}, width=0.8\\textwidth, height={Math.Max(4, 1 + kinds.Count * 0.8).ToString("0.#", CultureInfo.InvariantCulture)}cm, bar width=8pt, yticklabel style={{font=\\footnotesize}}, legend style={{font=\\footnotesize, at={{(0.5,-0.45)}}, anchor=north, legend columns=3, draw=none}}]");
        bool academic = kinds.Any(k => k.Tier == 0);
        foreach (var (tier, colour) in new[] { (1, "blue!60"), (2, "teal!50"), (3, "orange!50"), (0, "gray!50") }.Where(t => academic || t.Item1 != 0))
        {
            string points = string.Join(" ", kinds.Select((k, i) => $"({(k.Tier == tier ? k.Sources : 0)},{i + 1})"));
            sb.AppendLine($"\\addplot[fill={colour}] coordinates {{{points}}};");
        }
        sb.AppendLine(academic ? @"\legend{1st tier, 2nd tier, 3rd tier, academic}" : @"\legend{1st tier, 2nd tier, 3rd tier}");
        sb.AppendLine(@"\end{axis}");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>The number of assessed sources per band of two quality points (0–1.5, 2–3.5, …, 20).</summary>
    public static IReadOnlyList<(int From, int Sources)> QualityBands(MultivocalQualityFile quality)
    {
        // Table 7 points are the grey sources'; an academic source is appraised with MMAT instead.
        var scored = quality.Sources.Where(q => q.Outcome != "Not assessed" && !q.Formal).Select(q => q.Points).ToList();
        int top = quality.MaxPoints;
        return Enumerable.Range(0, top / 2 + 1).Select(i => i * 2)
            .Select(from => (From: from, Sources: scored.Count(p => p >= from && (from == top ? p <= top : p < from + 2))))
            .ToList();
    }

    private static string QualityChart(MultivocalReportInput input)
    {
        var bands = QualityBands(input.Quality);
        var sb = new StringBuilder();
        int max = Math.Max(2, bands.Max(b => b.Sources) + 1);
        string ticks = string.Join(",", bands.Select(b => b.From));
        string points = string.Join(" ", bands.Select(b => $"({b.From},{b.Sources})"));
        int threshold = input.Quality.Threshold;
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.8]");
        sb.AppendLine($"\\begin{{axis}}[ybar, xtick={{{ticks}}}, xmin=-1, xmax={input.Quality.MaxPoints + 1}, ymin=0, ymax={max}, xlabel={{Points (bands of 2)}}, ylabel={{Sources}}, width=\\textwidth, height=5.5cm, bar width=7pt, nodes near coords, every node near coord/.append style={{font=\\scriptsize}}]");
        sb.AppendLine($"\\addplot[fill=blue!50] coordinates {{{points}}};");
        sb.AppendLine($"\\draw[red, dashed] (axis cs:{threshold - 1},0) -- (axis cs:{threshold - 1},{max}) node[pos=0.95, right, font=\\scriptsize] {{threshold {threshold}}};");
        sb.AppendLine(@"\end{axis}");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>
    /// The systematic map as counts: for every attribute, how many extracted sources state each value, counted from
    /// the findings that passed every check (a quote found in the kept page, and judged to support its value).
    /// </summary>
    public static PaperTable MapCounts(MultivocalReportInput input)
    {
        var map = FixedMap(input);
        var split = Split(input);
        var rows = new List<IReadOnlyList<string>>();
        // With a formal pool every count is given for each pool too (issue #459).
        IReadOnlyList<string> Row(MapAttribute a, string value, int sources, IReadOnlyList<int> references, IReadOnlyList<int> stating) => split == null
            ? new[] { a.Name, a.Question, value, sources.ToString(CultureInfo.InvariantCulture) }
            : value == "not stated" && references.Count == 0
                ? new[] { a.Name, a.Question, value, (split.Grey - split.GreyOf(stating)).ToString(CultureInfo.InvariantCulture), (split.Academic - split.AcademicOf(stating)).ToString(CultureInfo.InvariantCulture), sources.ToString(CultureInfo.InvariantCulture) }
                : new[] { a.Name, a.Question, value, split.GreyOf(references).ToString(CultureInfo.InvariantCulture), split.AcademicOf(references).ToString(CultureInfo.InvariantCulture), sources.ToString(CultureInfo.InvariantCulture) };
        foreach (var attribute in map.Attributes)
        {
            var counts = ValueCounts(input, attribute);
            var stating = counts.SelectMany(c => c.References).Distinct().ToList();
            var (shown, namedOnce) = Compact(counts, attribute.Open);
            int first = rows.Count;
            foreach (var (value, sources, references) in shown)
                rows.Add(Row(attribute, value, sources, references, stating));
            if (namedOnce.Count > 0)
            {
                // The sources that name them, not the number of names, and before "not stated", as in the answer's own table.
                var naming = namedOnce.SelectMany(c => c.References).Distinct().ToList();
                int at = rows.FindIndex(first, r => r[2] == "not stated");
                rows.Insert(at < 0 ? rows.Count : at, Row(attribute, $"{namedOnce.Count} more, each named by one source", naming.Count, naming, stating));
            }
        }
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        return new PaperTable("The systematic map as counts", split == null ? new[] { "Attribute", "Question", "Value", "Sources" } : new[] { "Attribute", "Question", "Value", "Grey", "Academic", "Both" }, rows,
            $"Counted in code from the {extracted} extracted sources{(split == null ? "" : $" ({split.Grey} grey and {split.Academic} academic)")}; a source with several values counts once for each, and only values whose quote was found in the kept page and judged to support the value count." +
            (OpenNameMerger.Summary(input.Extraction) is { } merged ? " " + merged : ""));
    }

    /// <summary>
    /// How many extracted sources state each value of one attribute, with their reference numbers, most first; the
    /// sources that state none of its values are counted as "not stated". For an open attribute the values are the
    /// ones the sources name.
    /// </summary>
    public static IReadOnlyList<(string Value, int Sources, IReadOnlyList<int> References)> ValueCounts(MultivocalReportInput input, MapAttribute attribute)
    {
        var numbers = MultivocalReporter.SourceNumbers(input.Extraction);
        var findings = input.Synthesis.Findings
            .Where(f => f.Attribute == attribute.Name && numbers.ContainsKey(f.Address) && !MultivocalSynthesiser.IsNotStated(f.Value))
            .ToList();
        var counts = findings
            .GroupBy(f => f.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                IReadOnlyList<int> references = g.Select(f => numbers[f.Address]).Distinct().OrderBy(n => n).ToList();
                return (Value: g.First().Value.Trim(), Sources: references.Count, References: references);
            })
            .OrderByDescending(x => x.Sources).ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int stating = findings.Select(f => f.Address).Distinct().Count();
        int notStated = numbers.Count - stating;
        if (notStated > 0) counts.Add(("not stated", notStated, Array.Empty<int>()));
        return counts;
    }
}

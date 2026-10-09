using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>A table of the paper drawn in code: its caption, its columns and rows, and an optional note under it.</summary>
public sealed record PaperTable(string Caption, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string? Note = null);

/// <summary>
/// The multivocal review's own parts of the shared paper (MLR G-7), all written in code from the run's files, never
/// by the model: the method by the guidelines' phases; the data section (the flow of sources, the sources by kind and
/// outlet tier, the quality scores and the systematic map as counts); the answers to questions about frequency or
/// existence, as tables of the map's values with the sources that state each one; and the note under each theme on
/// what it rests on and where its sources disagree. <see cref="ReviewPaper"/> takes them in its slots; the LaTeX is
/// written with the invariant culture, which the caller sets, as for the systematic review.
/// </summary>
public static class MultivocalPaperParts
{
    /// <summary>
    /// Whether a question of this type is answered in code from the map's counts rather than written by the model:
    /// "how often" (frequency) and "does it exist" (existence) are counts, and counting is code's job.
    /// </summary>
    public static bool AnsweredInCode(string? questionType) => questionType is "existence" or "frequency";

    // ---------- Method ----------

    /// <summary>The method, one subsection per phase, from the same code as the report's method.</summary>
    public static List<PaperSection> Methods(MultivocalReportInput input) =>
        MultivocalReporter.MethodSections(input).Select(ToPaperSection).ToList();

    /// <summary>A report section as a paper subsection: its plain text, and its LaTeX with tables drawn to fit a column.</summary>
    public static PaperSection ToPaperSection(ReportSection section)
    {
        string heading = Regex.Replace(section.Heading, @"^\d+\.\s*", "");
        var text = new StringBuilder();
        var latex = new StringBuilder();
        foreach (var block in section.Blocks)
        {
            switch (block.Kind)
            {
                case "list":
                    foreach (var item in block.Items) text.AppendLine($"- {item}");
                    latex.AppendLine(@"\begin{itemize}");
                    foreach (var item in block.Items) latex.AppendLine(@"\item " + Prose(item));
                    latex.AppendLine(@"\end{itemize}");
                    break;
                case "table":
                    text.AppendLine(string.Join(" | ", block.Headers));
                    foreach (var row in block.Rows) text.AppendLine(string.Join(" | ", row));
                    latex.Append(ColumnTable(new PaperTable("", block.Headers, block.Rows)));
                    break;
                default:
                    text.AppendLine(block.Text).AppendLine();
                    latex.AppendLine(Prose(block.Text)).AppendLine();
                    break;
            }
        }
        return new PaperSection(heading, text.ToString().Trim(), latex.ToString());
    }

    // ---------- Data ----------

    /// <summary>The counts behind the data section, from the run's files.</summary>
    public sealed record SourceFlow(int Found, int Searches, int Duplicates, int NotScreened, int Screened, int Excluded, int Included,
        int BelowThreshold, int NotAssessed, int Passed, int NotExtracted, int Extracted, int Findings, int Themes);

    public static SourceFlow Flow(MultivocalReportInput input)
    {
        var s = input.Screening;
        var q = input.Quality;
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        return new SourceFlow(
            Found: input.Ledger.Sources.Count,
            Searches: input.Ledger.Searches.Count(x => x.Error == null),
            Duplicates: s.Count("Duplicate"),
            NotScreened: s.Count("Error"),
            Screened: s.Count("Included") + s.Count("Excluded"),
            Excluded: s.Count("Excluded"),
            Included: s.Count("Included"),
            BelowThreshold: q.Count("Below threshold"),
            NotAssessed: q.Count("Not assessed"),
            Passed: q.Count("Passed"),
            NotExtracted: input.Extraction.Sources.Count - extracted,
            Extracted: extracted,
            Findings: input.Synthesis.Findings.Count,
            Themes: input.Synthesis.Themes.Count);
    }

    /// <summary>The opening text of the data section, in code.</summary>
    public static string DataText(MultivocalReportInput input)
    {
        var f = Flow(input);
        return $"The searches found {f.Found} grey sources. After {f.Duplicates} duplicates were set aside, {f.Screened} were screened and {f.Included} included; " +
               $"{f.Passed} of them passed the quality threshold of {input.Quality.Threshold} points and {f.Extracted} were extracted against the systematic map. " +
               $"The figures below show the flow of sources, the extracted sources by kind and outlet tier, and the quality scores; the table shows how many sources state each value of the map.";
    }

    /// <summary>The data section's figures and table, drawn in code: full width, between the two-column parts.</summary>
    public static string DataLatex(MultivocalReportInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{figure}[H]");
        sb.AppendLine(@"\centering");
        sb.Append(FlowTikz(Flow(input)));
        sb.AppendLine(@"\caption{Flow of sources from the searches to the synthesis}");
        sb.AppendLine(@"\end{figure}");
        sb.AppendLine(@"\vspace{6pt}");

        sb.AppendLine(@"\begin{figure}[H]");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{minipage}{0.48\textwidth}");
        sb.AppendLine(@"\centering");
        sb.Append(KindChart(input));
        sb.AppendLine(@"\caption{Extracted sources by kind (bar colour: outlet tier)}");
        sb.AppendLine(@"\end{minipage}\hfill");
        sb.AppendLine(@"\begin{minipage}{0.48\textwidth}");
        sb.AppendLine(@"\centering");
        sb.Append(QualityChart(input));
        sb.AppendLine(@"\caption{Quality scores of the assessed sources (Table 7 checklist)}");
        sb.AppendLine(@"\end{minipage}");
        sb.AppendLine(@"\end{figure}");
        sb.AppendLine(@"\vspace{6pt}");

        sb.Append(WideTable(MapCounts(input)));
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    private static string FlowTikz(SourceFlow f)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[node distance=0.55cm and 1.2cm, box/.style={draw, rectangle, align=center, font=\small, minimum width=5.2cm, minimum height=0.8cm}, side/.style={draw, rectangle, align=left, font=\footnotesize, text width=4.6cm, fill=black!3}, >={Stealth}]");
        sb.AppendLine($@"\node[box] (found) {{Found: {f.Found} sources\\{{\footnotesize from {f.Searches} searches}}}};");
        sb.AppendLine($@"\node[box, below=of found] (screened) {{Screened twice: {f.Screened}}};");
        sb.AppendLine($@"\node[box, below=of screened] (included) {{Included: {f.Included}}};");
        sb.AppendLine($@"\node[box, below=of included] (passed) {{Passed the quality threshold: {f.Passed}}};");
        sb.AppendLine($@"\node[box, below=of passed] (extracted) {{Extracted against the map: {f.Extracted}}};");
        sb.AppendLine($@"\node[box, below=of extracted] (themes) {{Synthesised: {f.Findings} findings in {f.Themes} themes}};");
        sb.AppendLine($@"\node[side, right=of found] (dup) {{Duplicates: {f.Duplicates}\\Could not be screened: {f.NotScreened}}};");
        sb.AppendLine($@"\node[side, right=of screened] (exc) {{Excluded: {f.Excluded}}};");
        sb.AppendLine($@"\node[side, right=of included] (low) {{Below the threshold: {f.BelowThreshold}\\Not assessed (no readable page): {f.NotAssessed}}};");
        sb.AppendLine($@"\node[side, right=of passed] (failed) {{Could not be extracted: {f.NotExtracted}}};");
        sb.AppendLine(@"\draw[->] (found) -- (screened); \draw[->] (screened) -- (included); \draw[->] (included) -- (passed); \draw[->] (passed) -- (extracted); \draw[->] (extracted) -- (themes);");
        sb.AppendLine(@"\draw[->] (found) -- (dup); \draw[->] (screened) -- (exc); \draw[->] (included) -- (low); \draw[->] (passed) -- (failed);");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>The extracted sources per kind of grey literature, with their outlet tier, most first.</summary>
    public static IReadOnlyList<(string Kind, string Label, int Tier, int Sources)> Kinds(MultivocalReportInput input)
    {
        var records = input.Ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record);
        return input.Extraction.Sources.Where(s => s.Error == null)
            .GroupBy(s => records.GetValueOrDefault(s.Address)?.Kind ?? "")
            .Select(g => (Kind: g.Key, Label: MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == g.Key).Label ?? (g.Key.Length > 0 ? g.Key : "Other"),
                          Tier: MultivocalSynthesiser.TierOf(g.Key), Sources: g.Count()))
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
        foreach (var (tier, colour) in new[] { (1, "blue!60"), (2, "teal!50"), (3, "orange!50") })
        {
            string points = string.Join(" ", kinds.Select((k, i) => $"({(k.Tier == tier ? k.Sources : 0)},{i + 1})"));
            sb.AppendLine($"\\addplot[fill={colour}] coordinates {{{points}}};");
        }
        sb.AppendLine(@"\legend{1st tier, 2nd tier, 3rd tier}");
        sb.AppendLine(@"\end{axis}");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>The number of assessed sources per band of two quality points (0–1.5, 2–3.5, …, 20).</summary>
    public static IReadOnlyList<(int From, int Sources)> QualityBands(MultivocalQualityFile quality)
    {
        var scored = quality.Sources.Where(q => q.Outcome != "Not assessed").Select(q => q.Points).ToList();
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
        var rows = new List<IReadOnlyList<string>>();
        foreach (var attribute in map.Attributes)
        {
            var (shown, namedOnce) = Compact(ValueCounts(input, attribute), attribute.Open);
            foreach (var (value, sources, _) in shown)
                rows.Add(new[] { attribute.Name, attribute.Question, value, sources.ToString(CultureInfo.InvariantCulture) });
            if (namedOnce.Count > 0)
                rows.Add(new[] { attribute.Name, attribute.Question, $"{namedOnce.Count} more, each named by one source", namedOnce.Count.ToString(CultureInfo.InvariantCulture) });
        }
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        return new PaperTable("The systematic map as counts", new[] { "Attribute", "Question", "Value", "Sources" }, rows,
            $"Counted in code from the {extracted} extracted sources; a source with several values counts once for each, and only values whose quote was found in the kept page and judged to support the value count.");
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
            .Select(g => (Value: g.First().Value.Trim(), References: (IReadOnlyList<int>)g.Select(f => numbers[f.Address]).Distinct().OrderBy(n => n).ToList()))
            .Select(x => (x.Value, Sources: x.References.Count, x.References))
            .OrderByDescending(x => x.Sources).ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int stating = findings.Select(f => f.Address).Distinct().Count();
        int notStated = numbers.Count - stating;
        if (notStated > 0) counts.Add(("not stated", notStated, Array.Empty<int>()));
        return counts;
    }

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

    // ---------- LaTeX tables ----------

    /// <summary>A table that fits one of the paper's two columns: equal paragraph columns, small type.</summary>
    public static string ColumnTable(PaperTable table)
    {
        if (table.Headers.Count == 0) return "";
        var sb = new StringBuilder();
        // Each column gets an equal share of the line less its padding (3pt a side), so the table never runs into the next column.
        string width = (1.0 / table.Headers.Count).ToString("0.###", CultureInfo.InvariantCulture);
        sb.AppendLine(@"\par\smallskip\noindent{\scriptsize\setlength{\tabcolsep}{3pt}");
        if (table.Caption.Length > 0) sb.AppendLine(@"\textbf{" + PaperLatex.Escape(table.Caption) + @"}\\[2pt]");
        sb.AppendLine(@"\begin{tabular}{@{}" + string.Concat(table.Headers.Select(_ => $@">{{\raggedright\arraybackslash}}p{{\dimexpr{width}\linewidth-6pt\relax}}")) + "@{}}");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(string.Join(" & ", table.Headers.Select(h => @"\textbf{" + PaperLatex.Escape(h) + "}")) + @" \\");
        sb.AppendLine(@"\midrule");
        foreach (var row in table.Rows) sb.AppendLine(string.Join(" & ", row.Select(c => PaperLatex.Escape(c))) + @" \\");
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabular}");
        if (!string.IsNullOrWhiteSpace(table.Note)) sb.AppendLine(@"\\[2pt]\textit{" + Prose(table.Note!) + "}");
        sb.AppendLine(@"}\par\smallskip");
        return sb.ToString();
    }

    /// <summary>A full-width table that can run over pages (ltablex), for the data section between the two-column parts.</summary>
    public static string WideTable(PaperTable table)
    {
        if (table.Headers.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(@"\small");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{" + string.Concat(table.Headers.Select(_ => "X")) + "}");
        sb.AppendLine($@"\multicolumn{{{table.Headers.Count}}}{{l}}{{\textbf{{" + PaperLatex.Escape(table.Caption) + @"}} \\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(string.Join(" & ", table.Headers.Select(h => @"\textbf{" + PaperLatex.Escape(h) + "}")) + @" \\");
        sb.AppendLine(@"\midrule");
        sb.AppendLine(@"\endhead");
        foreach (var row in table.Rows) sb.AppendLine(string.Join(" & ", row.Select(c => PaperLatex.Escape(c))) + @" \\");
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        if (!string.IsNullOrWhiteSpace(table.Note)) sb.AppendLine(@"\vspace{4pt}\noindent\small\textit{" + Prose(table.Note!) + "}");
        sb.AppendLine(@"\normalsize");
        return sb.ToString();
    }

    /// <summary>
    /// Prose for LaTeX: <see cref="PaperLatex.Escape"/>, which also trims a trailing full stop (it cleans the ends of
    /// reference lines), with the full stop put back.
    /// </summary>
    public static string Prose(string text) => PaperLatex.Escape(text) + (text.TrimEnd().EndsWith('.') ? "." : "");

    private static MapVersion FixedMap(MultivocalReportInput input) =>
        input.Map.Versions.FirstOrDefault(v => v.Number == input.Map.FixedVersion) ?? input.Map.Latest;

    private static string Sources(int n) => n == 1 ? "1 source" : $"{n} sources";

    private static string Sentence(string text)
    {
        string t = text.Trim();
        return t.Length == 0 ? "" : t.EndsWith('.') ? t[..^1] : t;
    }

    private static string Short(string label) => label.Length <= 28 ? label : label[..26].TrimEnd() + "…";
}

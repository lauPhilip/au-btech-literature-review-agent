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
public static partial class MultivocalPaperParts
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
        if (input.Ledger.HasFormalPool) return MultivocalFormalText.DataText(input);
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
        sb.Append(input.Ledger.HasFormalPool ? MultivocalFormalText.FlowTikz(input) : FlowTikz(Flow(input)));
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

    public static string FlowTikz(SourceFlow f)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[node distance=0.55cm and 1.2cm, box/.style={draw, rectangle, align=center, font=\small, minimum width=5.2cm, minimum height=0.8cm}, side/.style={draw, rectangle, align=left, font=\footnotesize, text width=4.6cm, fill=black!3}, >={Stealth}]");
        sb.AppendLine($@"\node[box] (found) {{Found: {f.Found} sources\\{{\footnotesize from {f.Searches} searches}}}};");
        sb.AppendLine($@"\node[box, below=of found] (screened) {{Screened twice: {f.Screened}}};");
        sb.AppendLine($@"\node[box, below=of screened] (included) {{Included: {f.Included}}};");
        sb.AppendLine($@"\node[box, below=of included] (passed) {{Passed the quality threshold: {f.Passed}}};");
        sb.AppendLine($@"\node[box, below=of passed] (extracted) {{Extracted against the map: {f.Extracted}}};");
        sb.AppendLine($@"\node[box, below=of extracted] (themes) {{Synthesised: {f.Findings} finding{(f.Findings == 1 ? "" : "s")} in {f.Themes} theme{(f.Themes == 1 ? "" : "s")}}};");
        sb.AppendLine($@"\node[side, right=of found] (dup) {{Duplicates: {f.Duplicates}\\Could not be screened: {f.NotScreened}}};");
        sb.AppendLine($@"\node[side, right=of screened] (exc) {{Excluded: {f.Excluded}}};");
        sb.AppendLine($@"\node[side, right=of included] (low) {{Below the threshold: {f.BelowThreshold}\\Not assessed (no readable page): {f.NotAssessed}}};");
        sb.AppendLine($@"\node[side, right=of passed] (failed) {{Could not be extracted: {f.NotExtracted}}};");
        sb.AppendLine(@"\draw[->] (found) -- (screened); \draw[->] (screened) -- (included); \draw[->] (included) -- (passed); \draw[->] (passed) -- (extracted); \draw[->] (extracted) -- (themes);");
        sb.AppendLine(@"\draw[->] (found) -- (dup); \draw[->] (screened) -- (exc); \draw[->] (included) -- (low); \draw[->] (passed) -- (failed);");
        sb.AppendLine(@"\end{tikzpicture}");
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

    /// <summary>Prose for LaTeX: <see cref="PaperLatex.Escape"/>, which keeps the closing full stop.</summary>
    public static string Prose(string text) => PaperLatex.Escape(text);

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

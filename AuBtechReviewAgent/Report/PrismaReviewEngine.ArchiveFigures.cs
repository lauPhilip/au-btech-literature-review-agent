using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>The figures of the systematic review's main.tex: the flow diagram, papers per year, platforms and venues.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>Section 3's figures, drawn in code from the included papers and the run's counters.</summary>
    private static string DataFiguresLatex(List<IncludedPaperMetricRow> records, ReviewState? state)
    {
        int journalCount = records.Count(r => r.VenueType.Equals("Journals", StringComparison.OrdinalIgnoreCase));
        int conferenceCount = records.Count(r => r.VenueType.Equals("Conferences", StringComparison.OrdinalIgnoreCase));
        int transactionsCount = records.Count(r => r.VenueType.Equals("Transactions", StringComparison.OrdinalIgnoreCase));
        int proceedingsCount = records.Count(r => r.VenueType.Equals("Proceedings", StringComparison.OrdinalIgnoreCase));
        int preprintCount = records.Count(r => r.VenueType.Equals("Preprints", StringComparison.OrdinalIgnoreCase) || r.VenueType.Equals("Other", StringComparison.OrdinalIgnoreCase));

        // One slice per source that actually contributed papers (any source, including ones added later).
        var platformSlices = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Other" : r.Category)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select((g, i) => (Label: CategoryLabel(g.Key), Count: g.Count(), Color: PieColors[i % PieColors.Length]))
            .ToList();

        // Year axis built from the included papers themselves. It used to be fixed at 2023-2026, so papers
        // from other years (or with no year) silently disappeared from the chart.
        var publicationsByYear = records
            .GroupBy(r => r.Year > 0 ? r.Year.ToString() : "nodate")
            .OrderBy(g => g.Key == "nodate" ? "9999" : g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
        if (publicationsByYear.Count == 0) publicationsByYear[DateTime.UtcNow.Year.ToString()] = 0;
        // Plotted at x = 1..n with the years as tick labels. (pgfplots symbolic coords cannot hold "n.d.".)
        var yearKeys = publicationsByYear.Keys.ToList();
        string yearTicks = string.Join(",", Enumerable.Range(1, yearKeys.Count));
        string yearLabels = string.Join(",", yearKeys.Select(k => k == "nodate" ? "n.d." : k));
        string yearPoints = string.Join(" ", yearKeys.Select((k, i) => string.Create(CultureInfo.InvariantCulture, $"({i + 1},{publicationsByYear[k]})")));
        int yearAxisMax = Math.Max(4, publicationsByYear.Values.DefaultIfEmpty(0).Max() + 1);

        var sb = new StringBuilder();
        sb.AppendLine((state != null ? @"Figure~\ref{fig:flow} shows the flow of records through the review, and " : "")
            + @"Figure~\ref{fig:years} shows the included papers by year, Figure~\ref{fig:platforms} by the database that returned them and Figure~\ref{fig:venues} by venue.");
        sb.AppendLine();
        if (state != null)
        {
            var flow = PrismaFlowCounts.From(state);
            sb.AppendLine(@"\begin{figure}[H]");
            sb.AppendLine(@"\centering");
            sb.AppendLine(PrismaFlowDiagram.ToTikz(flow));
            sb.AppendLine(@"\caption{PRISMA 2020 flow diagram of this run" + (flow.IsConsistent ? "" : " (note: the run's counters do not fully add up; see transparent-process.json)") + @"}\label{fig:flow}");
            sb.AppendLine(@"\end{figure}");
            sb.AppendLine(@"\vspace{6pt}");
        }

        sb.AppendLine(@"\begin{figure}[H]");
        sb.AppendLine(@"\centering");

        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.65]");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"\\begin{{axis}}[ybar, xtick={{{yearTicks}}}, xticklabels={{{yearLabels}}}, xmin=0.5, xmax={yearKeys.Count + 0.5}, ymin=0, ymax={yearAxisMax}, ylabel={{Papers}}, xlabel={{Year}}, nodes near coords, width=\\textwidth, height=5.5cm, bar width=10pt]"));
        sb.AppendLine($"\\addplot[fill=blue!50] coordinates {{{yearPoints}}};");
        sb.AppendLine(@"\end{axis}");
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Papers per Year}\label{fig:years}");
        sb.AppendLine(@"\end{minipage}\hfill");

        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.55]");
        sb.Append(LatexPie(platformSlices));
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Platform Distribution}\label{fig:platforms}");
        sb.AppendLine(@"\end{minipage}\hfill");

        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.55]");
        sb.Append(LatexPie(new List<(string, int, string)>
        {
            ("Journals", journalCount, "blue!50"), ("Conferences", conferenceCount, "customIndigo!50"),
            ("Transactions", transactionsCount, "teal!50"), ("Proceedings", proceedingsCount, "orange!50"),
            ("Preprints", preprintCount, "red!50"),
        }));
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Venue Share}\label{fig:venues}");
        sb.AppendLine(@"\end{minipage}");
        sb.AppendLine(@"\end{figure}");
        sb.AppendLine(@"\vspace{10pt}");
        // \small used to come with the tables of the studies and stays here, so the rest of the paper keeps its text size.
        sb.AppendLine(@"\small");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    private static readonly string[] PieColors = { "teal!40", "orange!40", "blue!30", "yellow!40", "purple!30", "red!30", "green!30", "gray!30" };

    /// <summary>TikZ pie with a legend. A single slice is drawn as a full circle (a 0-360 degree arc renders as a line).</summary>
    public static string LatexPie(IReadOnlyList<(string Label, int Count, string Color)> slices)
    {
        var sb = new StringBuilder();
        double total = slices.Sum(x => x.Count);
        if (total <= 0)
        {
            sb.AppendLine(@"\draw[fill=gray!20] (0,0) circle (1.2cm); \node at (0,0) {No Data};");
            return sb.ToString();
        }
        double start = 0;
        foreach (var (label, count, color) in slices.Where(x => x.Count > 0))
        {
            double d = count / total * 360;
            if (d >= 359.999)
                sb.AppendLine($"\\draw[fill={color}] (0,0) circle (1.2cm); \\node at (0,0) {{\\scriptsize {count}}};");
            else
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"\\draw[fill={color}] (0,0) -- ({start:0.###}:1.2cm) arc ({start:0.###}:{start + d:0.###}:1.2cm) -- cycle; \\node at ({start + d / 2:0.###}:0.8cm) {{\\scriptsize {count}}};"));
            start += d;
        }
        sb.AppendLine(@"\matrix [draw,below,matrix of nodes,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {");
        var cells = slices.Select(x => $"\\node[fill={x.Color},label=right:({x.Count}) {EscapeLatexLabel(x.Label)}] {{}};").ToList();
        for (int i = 0; i < cells.Count; i += 2)
            sb.AppendLine(i + 1 < cells.Count ? $"{cells[i]} & {cells[i + 1]} \\\\" : $"{cells[i]} & \\\\");
        sb.AppendLine(@"};");
        return sb.ToString();
    }

    private static string EscapeLatexLabel(string s) => PaperLatex.Escape(s);
}

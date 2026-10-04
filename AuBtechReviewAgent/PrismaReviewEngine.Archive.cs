using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;
/// <summary>The downloadable archive: main.tex, bibliography, ledgers, manifest and source files.</summary>
public partial class PrismaReviewEngine
{
    private string EscapeLatexText(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        string cleanInput = input.Replace("https://doi.org/XXXX-XXXXXX", "")
                                 .Replace("https://doi.org/XXXXXXX.XXXXXXX", "")
                                 .Replace("https://doi.org/XX.XXXX/", "")
                                 .Replace("https://doi.org/XXXX", "")
                                 .Replace("https://doi.org/XXX", "")
                                 .Replace("XXXXXX.XXXXX", "")
                                 .TrimEnd(' ', ',', '.', '/');

        string escaped = cleanInput.Replace(@"\", @"\textbackslash ")
                         .Replace("&", @"\&")
                         .Replace("%", @"\%")
                         .Replace("$", @"\$")
                         .Replace("_", @"\_")
                         .Replace("#", @"\#")
                         .Replace("{", @"\{")
                         .Replace("}", @"\}")
                         .Replace("~", @"\textasciitilde ")
                         .Replace("^", @"\textasciicircum ");
        // Straight double quotes print as two closing quotes in LaTeX; use proper ``opening'' and closing quotes.
        escaped = Regex.Replace(escaped, "\"([^\"\n]*)\"", "``$1''");
        // Markdown-style *italics* (used for venues in the APA references) -> real LaTeX italics.
        return Regex.Replace(escaped, @"\*([^*\n]+)\*", @"\textit{$1}");
    }
    
    /// <summary>
    /// Loads the persisted report + synthesized records for a session straight off disk and builds the
    /// workspace archive from them. Lets the download be served from a plain HTTP endpoint (Program.cs)
    /// instead of round-tripping the whole zip through the Blazor Server SignalR connection, which has a
    /// small default max message size and would silently fail once real source PDFs are included.
    /// </summary>
    public byte[] GenerateWorkspaceArchiveFromDisk(Guid sessionId)
    {
        var state = LoadState(sessionId);
        if (state == null) return Array.Empty<byte>();

        var report = new PrismaReport();
        string reportPath = GetReportFilePath(sessionId);
        if (File.Exists(reportPath))
        {
            try
            {
                string content = File.ReadAllText(reportPath);
                report = JsonSerializer.Deserialize<PrismaReport>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrismaReport();
            }
            catch (Exception ex)
            {
                // The archive is still built from the ledger; only the report section is missing.
                _log.LogWarning("Archive: could not read prisma-report.json: {Message}", SanitizeLogMessage(ex.Message));
            }
        }

        return GenerateWorkspaceArchive(sessionId, report, state.SynthesizedRecords, state);
    }

    /// <summary>references.bib / references.ris for a run, or null if the run does not exist.</summary>
    public string? ExportReferences(Guid sessionId, string format)
    {
        var state = LoadState(sessionId);
        if (state == null) return null;
        return format == "ris" ? BibliographyExporter.ToRis(state.SynthesizedRecords) : BibliographyExporter.ToBibTeX(state.SynthesizedRecords);
    }

    // NOTE: historically named GenerateManuscriptPdf, but it never compiled a PDF - it always returned a
    // zip bundle (LaTeX source + JSON ledger + source PDFs). Renamed to match what it actually produces.
    public byte[] GenerateWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state = null)
    {
        // The LaTeX below interpolates decimals (pie-slice angles, axis limits). On a machine with a Danish
        // (or any comma-decimal) culture those came out as "51,43", which breaks the TikZ syntax, so the
        // document is always written with the invariant culture.
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return BuildWorkspaceArchive(sessionId, report, records, state);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private byte[] BuildWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state)
    {
        // main.tex is built in memory and written straight into the zip. It used to go through a temporary
        // manuscript_*.tex file in the project folder, and every download first deleted all such files - so
        // two people downloading at the same moment could delete each other's main.tex.

        // Table 3.1 and the bibliography must follow the reference numbers the synthesis cites.
        // (Older runs have ReferenceNumber = 0 everywhere; OrderBy is stable, so they keep their order.)
        records = records.OrderBy(r => r.ReferenceNumber).ToList();

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
        string yearPoints = string.Join(" ", yearKeys.Select((k, i) => $"({i + 1},{publicationsByYear[k]})"));
        int yearAxisMax = Math.Max(4, publicationsByYear.Values.DefaultIfEmpty(0).Max() + 1);

        string sanitizedTitleItem = EscapeLatexText((report.TitleItem ?? "Systematic Review Manuscript").Replace("[Source Context Anchor 1]", "").Trim());
        string sanitizedAbstractItem = EscapeLatexText(report.AbstractItem ?? "");
        string sanitizedRationaleItem = EscapeLatexText(report.RationaleItem ?? "");
        string sanitizedObjectivesItem = EscapeLatexText(report.ObjectivesItem ?? "");
        string sanitizedEligibilityItem = EscapeLatexText(report.EligibilityItem ?? "");
        string sanitizedSourcesItem = EscapeLatexText(report.SourcesItem ?? "");
        string sanitizedSearchStrategyItem = EscapeLatexText(report.SearchStrategyItem ?? "");
        string sanitizedSelectionProcessItem = EscapeLatexText(report.SelectionProcessItem ?? "");
        string sanitizedBiasAssessmentItem = EscapeLatexText(report.BiasAssessmentItem ?? "");
        
        string rawSynthesis = report.SynthesisResultsItem ?? "";
        string textSynthesisOnly = Regex.Replace(rawSynthesis, @"\[MERMAID_START\].*?\[MERMAID_END\]", "", RegexOptions.Singleline);
        textSynthesisOnly = Regex.Replace(textSynthesisOnly, @"\[TIKZ_START\].*?\[TIKZ_END\]", "", RegexOptions.Singleline).Trim();
        string sanitizedSynthesisResultsItem = EscapeLatexText(textSynthesisOnly);

        string tikzDiagramCode = "";
        var tikzMatch = Regex.Match(rawSynthesis, @"\[TIKZ_START\](.*?)\[TIKZ_END\]", RegexOptions.Singleline);
        if (tikzMatch.Success) {
            tikzDiagramCode = tikzMatch.Groups[1].Value.Trim();
            // "-->" is not a TikZ arrow tip (it made every generated main.tex fail to compile); only repair the
            // mangled "-¿" some model outputs contain.
            tikzDiagramCode = tikzDiagramCode.Replace("-¿", "->");
        }

        string sanitizedDiscussionItem = EscapeLatexText(report.DiscussionItem ?? "");
        string sanitizedSupportItem = EscapeLatexText(report.SupportItem ?? "");

        var sb = new StringBuilder();
        sb.AppendLine(@"\documentclass[9pt,a4paper]{article}");
        sb.AppendLine(@"\usepackage[utf8]{inputenc}");
        sb.AppendLine(@"\usepackage[margin=0.8in]{geometry}");
        sb.AppendLine(@"\usepackage{amsmath,amsfonts,amssymb}");
        sb.AppendLine(@"\usepackage{booktabs}");
        sb.AppendLine(@"\usepackage{ltablex}"); 
        sb.AppendLine(@"\usepackage{xcolor}");
        sb.AppendLine(@"\usepackage{fancyhdr}");
        sb.AppendLine(@"\usepackage{float}");
        sb.AppendLine(@"\usepackage{pgfplots}");
        sb.AppendLine(@"\usepgfplotslibrary{statistics}");
        sb.AppendLine(@"\pgfplotsset{compat=1.18}");
        sb.AppendLine(@"\usetikzlibrary{matrix,arrows.meta,positioning}");
        
        sb.AppendLine(@"\usepackage{tgtermes}"); 
        sb.AppendLine(@"\usepackage{tgheros}");  
        sb.AppendLine(@"\usepackage{tgcursor}");  
        sb.AppendLine(@"\usepackage{multicol}");
        
        sb.AppendLine(@"\definecolor{auBlue}{HTML}{003B5C}");
        sb.AppendLine(@"\definecolor{greyText}{HTML}{4B5563}");
        sb.AppendLine(@"\definecolor{customIndigo}{HTML}{312E81}");
        
        sb.AppendLine(@"\pagestyle{fancy}");
        sb.AppendLine(@"\fancyhf{}");
        sb.AppendLine(@"\renewcommand{\headrulewidth}{0pt}");
        sb.AppendLine(@"\fancyfoot[C]{\sffamily\scriptsize\color{greyText} Page \thepage}");

        sb.AppendLine(@"\begin{document}");

        sb.AppendLine(@"\begin{center}");
        sb.AppendLine(@"    {\sffamily\bfseries\scriptsize\color{gray} AI-GENERATED SYSTEMATIC LITERATURE REVIEW  \\ \vspace{4pt}}");
        sb.AppendLine("    {\\rmfamily\\LARGE\\bfseries " + sanitizedTitleItem + " \\\\ \\vspace{10pt}}");
        sb.AppendLine(@"    {\sffamily\small\color{greyText} Department of Business Development and Technology, Aarhus University \\ \vspace{4pt}}");
        string protocolHash = string.IsNullOrWhiteSpace(report.ProtocolHash) ? "not recorded" : report.ProtocolHash;
        sb.AppendLine("    {\\ttfamily\\scriptsize\\color{gray} Protocol Hash: " + protocolHash + " | Generated: " + (report.GeneratedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")) + " \\\\ \\vspace{15pt}}");
        sb.AppendLine(@"    \color{lightgray}\hrule\vspace{15pt}");
        sb.AppendLine(@"    \color{black}");
        sb.AppendLine(@"\end{center}");

        sb.AppendLine(@"\noindent\colorbox{black!4}{");
        sb.AppendLine(@"\parbox{\dimexpr\linewidth-2\fboxsep\relax}{");
        sb.AppendLine(@"    \small\sffamily\bfseries\color{auBlue} ABSTRACT \\ \vspace{4pt}");
        sb.AppendLine("    \\itshape\\rmfamily\\color{black} " + sanitizedAbstractItem);
        sb.AppendLine(@"}}");
        sb.AppendLine(@"\vspace{15pt}");

        sb.AppendLine(@"\begin{multicols}{2}");

        sb.AppendLine(@"\section{Introduction}");
        sb.AppendLine(@"\subsection{Rationale}");
        sb.AppendLine(sanitizedRationaleItem);
        sb.AppendLine(@"\subsection{Objectives}");
        sb.AppendLine(sanitizedObjectivesItem);

        sb.AppendLine(@"\section{Methodology}");
        sb.AppendLine(@"\subsection{Eligibility Criteria}");
        sb.AppendLine(sanitizedEligibilityItem);
        sb.AppendLine(@"\subsection{Information Sources}");
        sb.AppendLine(sanitizedSourcesItem);
        sb.AppendLine(@"\subsection{Search Execution}");
        sb.AppendLine(sanitizedSearchStrategyItem);
        sb.AppendLine(@"\subsection{Selection Automation}");
        sb.AppendLine(sanitizedSelectionProcessItem);
        sb.AppendLine(@"\subsection{Data Collection and Quality Appraisal}");
        sb.AppendLine(sanitizedBiasAssessmentItem);
        if (!string.IsNullOrWhiteSpace(report.SynthesisMethodsItem))
        {
            sb.AppendLine(@"\subsection{Synthesis Methods}");
            sb.AppendLine(EscapeLatexText(report.SynthesisMethodsItem));
        }

        sb.AppendLine(@"\end{multicols}");
        sb.AppendLine(@"\section{Data \& Collection Metrics}");
        sb.AppendLine(EscapeLatexText(MethodsSectionWriter.IncludedSet(records)));
        sb.AppendLine(@"\vspace{10pt}");

        if (state != null)
        {
            var flow = PrismaFlowCounts.From(state);
            sb.AppendLine(@"\begin{figure}[H]");
            sb.AppendLine(@"\centering");
            sb.AppendLine(PrismaFlowDiagram.ToTikz(flow));
            sb.AppendLine(@"\caption{PRISMA 2020 flow diagram of this run" + (flow.IsConsistent ? "" : " (note: the run's counters do not fully add up; see transparent-process.json)") + "}");
            sb.AppendLine(@"\end{figure}");
            sb.AppendLine(@"\vspace{6pt}");
        }

        sb.AppendLine(@"\begin{figure}[H]");
        sb.AppendLine(@"\centering");
        
        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.65]");
        sb.AppendLine($"\\begin{{axis}}[ybar, xtick={{{yearTicks}}}, xticklabels={{{yearLabels}}}, xmin=0.5, xmax={yearKeys.Count + 0.5}, ymin=0, ymax={yearAxisMax}, ylabel={{Papers}}, xlabel={{Year}}, nodes near coords, width=\\textwidth, height=5.5cm, bar width=10pt]");
        sb.AppendLine($"\\addplot[fill=blue!50] coordinates {{{yearPoints}}};");
        sb.AppendLine(@"\end{axis}");
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Papers per Year}");
        sb.AppendLine(@"\end{minipage}\hfill");

        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.55]");
        sb.Append(LatexPie(platformSlices));
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Platform Distribution}");
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
        sb.AppendLine(@"\caption{Venue Share}");
        sb.AppendLine(@"\end{minipage}");
        sb.AppendLine(@"\end{figure}");
        sb.AppendLine(@"\vspace{10pt}");

        sb.AppendLine(@"\small");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.6\hsize}X >{\hsize=1.2\hsize}X >{\hsize=1.0\hsize}X >{\hsize=0.5\hsize}X >{\hsize=1.7\hsize}X}");
        sb.AppendLine(@"\multicolumn{5}{l}{\textbf{Table 3.1: Systematic Synthesis Extraction Registry}} \\\\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Paper Name} & \textbf{Executive Summary} & \textbf{Inclusion Rationale} & \textbf{Category} & \textbf{Citation (APA 7th)} \\");
        sb.AppendLine(@"\midrule");
        foreach (var row in records)
        {
            string title = EscapeLatexText(row.Title);
            string summary = EscapeLatexText(row.Summary);
            string rationale = EscapeLatexText(row.InclusionRationale);
            string category = EscapeLatexText(row.Category);
            string citation = EscapeLatexText(row.ApaCitation);
            sb.AppendLine($"{title} & {summary} & {rationale} & {category} & {citation} \\\\ \\hline");
        }
        sb.AppendLine(@"\end{tabularx}");
        sb.AppendLine(@"\vspace{4pt}\noindent\small ");
        sb.AppendLine(@"\textbf{Table Overview:} The extraction logs archived inside Table 3.1 summarize the qualitative characteristics of each selected paper.");
        if (state?.Extractions is { Count: > 0 } extractions)
        {
            sb.AppendLine(@"\vspace{10pt}");
            sb.Append(ExtractionTables(extractions));
        }
        sb.AppendLine(@"\vspace{10pt}");

        sb.AppendLine(@"\begin{multicols}{2}");
        sb.AppendLine(@"\section{Results \& Synthesis}");
        sb.AppendLine(sanitizedSynthesisResultsItem);
        foreach (var section in report.SynthesisSections ?? new List<SynthesisSection>())
        {
            sb.AppendLine($"\\subsection{{{EscapeLatexText(section.Heading)}}}");
            sb.AppendLine(EscapeLatexText(section.Text));
        }
        if (!string.IsNullOrWhiteSpace(report.CoverageSummary))
        {
            sb.AppendLine(@"\par\smallskip\noindent\textit{" + EscapeLatexText(report.CoverageSummary) + "}");
        }
        if (state?.ThematicSynthesis is { Themes.Count: > 0 } book && (report.SynthesisSections?.Count ?? 0) > 0)
        {
            sb.AppendLine(@"\end{multicols}");
            sb.Append(EvidenceMapTable(book));
            sb.AppendLine(@"\begin{multicols}{2}");
        }
        if (report.Artifact is { Error: null } artifact)
        {
            sb.AppendLine(@"\end{multicols}");
            sb.Append(ArtifactLatex(artifact));
            sb.AppendLine(@"\begin{multicols}{2}");
        }
        
        if (!string.IsNullOrEmpty(tikzDiagramCode))
        {
            sb.AppendLine(@"\end{multicols}");
            sb.AppendLine(@"\begin{figure}[H]");
            sb.AppendLine(@"\centering");
            sb.AppendLine(@"\resizebox{\linewidth}{!}{");
            sb.AppendLine(tikzDiagramCode);
            sb.AppendLine(@"}");
            sb.AppendLine(@"\caption{Grounded Architectural Synthesis System Model Diagram}");
            sb.AppendLine(@"\end{figure}");
            sb.AppendLine(@"\begin{multicols}{2}");
        }

        sb.AppendLine(@"\section{Discussion}");
        sb.AppendLine(sanitizedDiscussionItem);
        if (!string.IsNullOrWhiteSpace(report.CitationCheckSummary))
        {
            sb.AppendLine(@"\subsection{Automated Citation Check}");
            sb.AppendLine(EscapeLatexText(report.CitationCheckSummary));
        }
        if (!string.IsNullOrWhiteSpace(state?.HumanScreeningReviewOutcome))
        {
            sb.AppendLine(@"\subsection{Human Screening Review}");
            sb.AppendLine(EscapeLatexText(state!.HumanScreeningReviewOutcome!));
        }

        sb.AppendLine(@"\section{Administrative Declarations}");
        sb.AppendLine(@"\subsection{Support \& Funding}");
        sb.AppendLine(sanitizedSupportItem);
        if (!string.IsNullOrWhiteSpace(report.ProtocolItem))
        {
            sb.AppendLine(@"\subsection{Registration and Protocol}");
            sb.AppendLine(EscapeLatexText(report.ProtocolItem));
        }
        sb.AppendLine(@"\subsection{Open Science Code Availability}");
        sb.AppendLine((report.AvailabilityItem ?? "").Replace("_", @"\_"));
        sb.AppendLine(@"\end{multicols}");

        sb.AppendLine(@"\clearpage");
        sb.AppendLine(@"\begin{thebibliography}{99}");
        int refIdx = 1;
        foreach (var row in records)
        {
            string citation = EscapeLatexText(row.ApaCitation);
            sb.AppendLine($"\\bibitem{{ref{refIdx}}} {citation}");
            refIdx++;
        }
        sb.AppendLine(@"\end{thebibliography}");
        sb.AppendLine(@"\end{document}");

        // Everything is collected first so manifest.json can list the SHA-256 of every file in the archive.
        var entries = new List<(string Path, byte[] Content)>();
        void AddText(string name, string content) => entries.Add((name, new UTF8Encoding(false).GetBytes(content)));
        void AddFile(string path, string name)
        {
            var bytes = ReadShared(path);
            if (bytes != null) entries.Add((name, bytes));
        }

        AddText("main.tex", sb.ToString());
        AddText("references.bib", BibliographyExporter.ToBibTeX(records));
        AddText("references.ris", BibliographyExporter.ToRis(records));

        string isolatedFolder = GetWorkspaceFolderPath(sessionId);
        // Audit files kept at the archive root, in this order.
        string[] rootFiles =
        {
            "protocol.md", "transparent-process.json", "prisma-report.json", "llm-calls.json", "extraction.json",
            "citation-audit.json", "thematic-codebook.json", "run-metrics.json", "peer-review-feedback.json", "stylistic-transformation-ledger.json", "grounded-outline.txt",
        };
        foreach (var name in rootFiles)
        {
            string path = Path.Join(isolatedFolder, name);
            if (File.Exists(path)) AddFile(path, name);
        }

        if (Directory.Exists(isolatedFolder))
        {
            // Downloaded full texts.
            foreach (var file in Directory.GetFiles(isolatedFolder).OrderBy(f => f, StringComparer.Ordinal))
            {
                string filename = Path.GetFileName(file);
                if (rootFiles.Contains(filename, StringComparer.OrdinalIgnoreCase)) continue;
                if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                AddFile(file, $"SourcePapers/{filename}");
            }
            // What each source returned, exactly as received.
            string rawFolder = Path.Join(isolatedFolder, RawResponsesFolder);
            if (Directory.Exists(rawFolder))
                foreach (var file in Directory.GetFiles(rawFolder).OrderBy(f => f, StringComparer.Ordinal))
                    AddFile(file, $"{RawResponsesFolder}/{Path.GetFileName(file)}");
        }

        AddText("manifest.json", RunManifest.Build(sessionId, state, entries));

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var target = entry.Open();
                target.Write(content, 0, content.Length);
            }
        }
        return memoryStream.ToArray();
    }

    /// <summary>The artifact as a TikZ figure, a table or a list, outside the two-column layout.</summary>
    private string ArtifactLatex(ReviewArtifact artifact)
    {
        var sb = new StringBuilder();
        string title = EscapeLatexText(string.IsNullOrWhiteSpace(artifact.Title) ? "Artifact" : artifact.Title);
        sb.AppendLine(@"\vspace{6pt}");
        if (artifact.IsDiagram)
        {
            sb.AppendLine(@"\begin{figure}[H]");
            sb.AppendLine(@"\centering");
            sb.AppendLine(@"\resizebox{\linewidth}{!}{");
            sb.Append(ArtifactBuilder.ToTikz(artifact, EscapeLatexText));
            sb.AppendLine(@"}");
            sb.AppendLine($"\\caption{{{title}. {EscapeLatexText(artifact.Caption)}}}");
            sb.AppendLine(@"\end{figure}");
        }
        else if (artifact.Kind == ArtifactKinds.Table && artifact.Columns.Count > 0)
        {
            string spec = string.Join(" ", artifact.Columns.Select(_ => "X"));
            sb.AppendLine($"\\begin{{tabularx}}{{\\textwidth}}{{{spec}}}");
            sb.AppendLine($"\\multicolumn{{{artifact.Columns.Count}}}{{l}}{{\\textbf{{Table 4.2: {title}}}}} \\\\");
            sb.AppendLine(@"\toprule");
            sb.AppendLine(string.Join(" & ", artifact.Columns.Select(c => $"\\textbf{{{EscapeLatexText(c)}}}")) + @" \\");
            sb.AppendLine(@"\midrule");
            foreach (var row in artifact.Rows)
                sb.AppendLine(string.Join(" & ", row.Select(EscapeLatexText)) + @" \\ \hline");
            sb.AppendLine(@"\end{tabularx}");
            if (!string.IsNullOrWhiteSpace(artifact.Caption))
                sb.AppendLine(@"\vspace{2pt}\noindent\small " + EscapeLatexText(artifact.Caption) + @"\normalsize");
        }
        else
        {
            sb.AppendLine($"\\noindent\\textbf{{{title}}}");
            if (!string.IsNullOrWhiteSpace(artifact.Caption)) sb.AppendLine(@"\par\noindent " + EscapeLatexText(artifact.Caption));
            sb.AppendLine(@"\begin{itemize}");
            foreach (var bullet in artifact.Bullets) sb.AppendLine(@"\item " + EscapeLatexText(bullet));
            sb.AppendLine(@"\end{itemize}");
        }
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    /// <summary>Evidence map: which studies contribute to which theme of the thematic synthesis.</summary>
    private string EvidenceMapTable(ThematicCodebook book)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\vspace{6pt}");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.9\hsize}X >{\hsize=1.4\hsize}X >{\hsize=0.7\hsize}X}");
        sb.AppendLine(@"\multicolumn{3}{l}{\textbf{Table 4.1: Evidence Map of the Thematic Synthesis}} \\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Theme} & \textbf{Description} & \textbf{Studies} \\");
        sb.AppendLine(@"\midrule");
        foreach (var t in book.Themes)
            sb.AppendLine($"{EscapeLatexText(t.Name)} & {EscapeLatexText(t.Description)} & {string.Join(", ", t.Studies.Select(r => $"{{[}}{r}{{]}}"))} \\\\ \\hline");
        sb.AppendLine(@"\end{tabularx}");
        var notes = new List<string>();
        if (book.SecondCoding?.Kappa is double k)
            notes.Add($"Second, independent coding: Cohen's kappa = {k.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} over {book.SecondCoding.CellsCompared} study-theme decisions.");
        if (book.Uncoded.Count > 0)
            notes.Add("Not placed in a theme: " + string.Join("; ", book.Uncoded.Select(u => $"[{u.Reference}] {u.Reason.TrimEnd('.')}")) + ".");
        if (notes.Count > 0)
            sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + EscapeLatexText(string.Join(" ", notes)) + @"\normalsize");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    /// <summary>Table 3.2 (extracted data, with a mark where the supporting quote was found) and Table 3.3 (MMAT).</summary>
    private string ExtractionTables(IReadOnlyList<StudyExtraction> extractions)
    {
        string V(EvidencedValue v) => EscapeLatexText(v.Value) + (v.QuoteVerified ? "" : (v.Value.Equals("not reported", StringComparison.OrdinalIgnoreCase) ? "" : "$^{\\dagger}$"));
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.25\hsize}X >{\hsize=0.8\hsize}X >{\hsize=1.1\hsize}X >{\hsize=0.9\hsize}X >{\hsize=1.95\hsize}X}");
        sb.AppendLine(@"\multicolumn{5}{l}{\textbf{Table 3.2: Extracted Study Data}} \\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Ref.} & \textbf{Study type} & \textbf{Method} & \textbf{Sample} & \textbf{Key findings} \\");
        sb.AppendLine(@"\midrule");
        foreach (var e in extractions)
        {
            if (e.Error != null)
            {
                sb.AppendLine($"{{[}}{e.ReferenceNumber}{{]}} & \\multicolumn{{4}}{{l}}{{Extraction failed.}} \\\\ \\hline");
                continue;
            }
            string findings = string.Join(" ", e.KeyFindings.Select((f, i) => $"({i + 1}) {V(f)}"));
            sb.AppendLine($"{{[}}{e.ReferenceNumber}{{]}} & {EscapeLatexText(e.StudyType)} & {V(e.Method)} & {V(e.Sample)} & {findings} \\\\ \\hline");
        }
        sb.AppendLine(@"\end{tabularx}");
        sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize $^{\dagger}$ The supporting quote given by the model was not found in the paper's text; treat the value with caution. Evidence: full text or abstract, see extraction.json.\normalsize");
        sb.AppendLine(@"\vspace{10pt}");

        string A(string answer) => answer switch { "yes" => "Yes", "no" => "No", _ => "?" };
        var appraised = extractions.Where(MethodsSectionWriter.ShowInAppraisalTable).ToList();
        if (appraised.Count > 0)
        {
            sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.4\hsize}X >{\hsize=1.6\hsize}X c c c c c c}");
            sb.AppendLine(@"\multicolumn{8}{l}{\textbf{Table 3.3: Quality Appraisal (MMAT 2018; Yes / No / ? = can't tell; Met = questions answered yes, not an overall score)}} \\");
            sb.AppendLine(@"\toprule");
            sb.AppendLine(@"\textbf{Ref.} & \textbf{Category} & \textbf{1} & \textbf{2} & \textbf{3} & \textbf{4} & \textbf{5} & \textbf{Met} \\");
            sb.AppendLine(@"\midrule");
            foreach (var e in appraised)
            {
                string category = e.Error != null ? "not appraised" : e.AppraisalCategory.Replace('_', ' ');
                var cells = Enumerable.Range(0, 5).Select(i => i < e.Appraisal.Count ? A(e.Appraisal[i].Answer) : "--");
                string met = e.Appraisal.Count == 0 ? "--" : $"{e.AppraisalYes}/{e.Appraisal.Count}";
                sb.AppendLine($"{{[}}{e.ReferenceNumber}{{]}} & {EscapeLatexText(category)} & {string.Join(" & ", cells)} & {met} \\\\");
            }
            sb.AppendLine(@"\bottomrule");
            sb.AppendLine(@"\end{tabularx}");
        }
        string notAppraised = MethodsSectionWriter.NotAppraisedNote(extractions, "--");
        if (notAppraised.Length > 0)
            sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + EscapeLatexText(notAppraised.TrimEnd('.')) + @".\normalsize");
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
                sb.AppendLine($"\\draw[fill={color}] (0,0) -- ({start:0.###}:1.2cm) arc ({start:0.###}:{start + d:0.###}:1.2cm) -- cycle; \\node at ({start + d / 2:0.###}:0.8cm) {{\\scriptsize {count}}};");
            start += d;
        }
        sb.AppendLine(@"\matrix [draw,below,matrix of nodes,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {");
        var cells = slices.Select(x => $"\\node[fill={x.Color},label=right:({x.Count}) {EscapeLatexLabel(x.Label)}] {{}};").ToList();
        for (int i = 0; i < cells.Count; i += 2)
            sb.AppendLine(i + 1 < cells.Count ? $"{cells[i]} & {cells[i + 1]} \\\\" : $"{cells[i]} & \\\\");
        sb.AppendLine(@"};");
        return sb.ToString();
    }

    private static string EscapeLatexLabel(string s) => s.Replace("&", @"\&").Replace("_", @"\_").Replace("%", @"\%").Replace("#", @"\#");

    // Reads with FileShare.ReadWrite so a download never fails because the run is writing its ledger.
    private byte[]? ReadShared(string path)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            return copy.ToArray();
        }
        catch (IOException ex)
        {
            _log.LogWarning("Archive: skipped {File}: {Message}", SanitizeLogMessage(Path.GetFileName(path)), SanitizeLogMessage(ex.Message));
            return null;
        }
    }
}

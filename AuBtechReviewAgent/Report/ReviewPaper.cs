using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// One section of a paper: a heading and its plain text, which the LaTeX builder escapes. A section whose text is
/// already LaTeX, built in code, carries it in <see cref="Latex"/> instead.
/// </summary>
public sealed record PaperSection(string Heading, string Text, string? Latex = null);

/// <summary>
/// The paper of a review, in one shape for every kind of review (module design M9). The shared parts are the same
/// for all of them: the title, abstract, introduction, results by theme, discussion, declarations and references.
/// What a module does differently goes into its own slots: its method sections, its data figures and tables, the
/// full-width results blocks (an evidence map, an artifact) and the notes after the discussion. Everything a module
/// puts in a LaTeX slot is drawn in code, never by the model.
/// </summary>
public sealed class ReviewPaper
{
    /// <summary>The line above the title, e.g. "AI-GENERATED SYSTEMATIC LITERATURE REVIEW".</summary>
    public string KindLabel { get; init; } = "";
    public string Title { get; init; } = "";
    public string Affiliation { get; init; } = "Department of Business Development and Technology, Aarhus University";
    public string ProtocolHash { get; init; } = "not recorded";
    public string GeneratedAt { get; init; } = "";
    public string Abstract { get; init; } = "";
    public string Rationale { get; init; } = "";
    public string Objectives { get; init; } = "";

    /// <summary>The method, one subsection per part, written in code from the run's ledger.</summary>
    public List<PaperSection> Methods { get; init; } = new();

    /// <summary>The data and collection section: its opening text, then its figures and tables as LaTeX built in code.</summary>
    public string DataHeading { get; init; } = "Data & Collection Metrics";
    public string DataText { get; init; } = "";
    public string DataLatex { get; init; } = "";

    /// <summary>The results: an overview, one subsection per theme, a note on which sources are cited, and full-width blocks.</summary>
    public string ResultsHeading { get; init; } = "Results & Synthesis";
    public string ResultsText { get; init; } = "";
    public List<PaperSection> Themes { get; init; } = new();
    public string? CoverageNote { get; init; }
    public List<string> ResultsLatex { get; init; } = new();

    public string Discussion { get; init; } = "";

    /// <summary>Subsections after the discussion, such as the automated citation check.</summary>
    public List<PaperSection> DiscussionNotes { get; init; } = new();

    /// <summary>Support, registration and protocol, availability.</summary>
    public List<PaperSection> Declarations { get; init; } = new();

    /// <summary>The reference list, in reference-number order, as plain text.</summary>
    public List<string> References { get; init; } = new();
}

/// <summary>
/// Writes a <see cref="ReviewPaper"/> as main.tex. The layout is the same for every kind of review: the title block,
/// the abstract box, the introduction and method in two columns, the data section full width, the results,
/// discussion and declarations in two columns, and the bibliography. Plain text is escaped here; LaTeX slots are
/// written as they are. Decimals in figures need the invariant culture, which the caller sets.
/// </summary>
public static class PaperLatex
{
    /// <summary>Escapes plain text for LaTeX, turns straight quotes into LaTeX quotes and *italics* into \textit.</summary>
    public static string Escape(string input)
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

    private static string Body(PaperSection section) => section.Latex ?? Escape(section.Text);

    public static string Build(ReviewPaper paper)
    {
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
        sb.AppendLine(@"    {\sffamily\bfseries\scriptsize\color{gray} " + paper.KindLabel + @"  \\ \vspace{4pt}}");
        sb.AppendLine("    {\\rmfamily\\LARGE\\bfseries " + Escape(paper.Title) + " \\\\ \\vspace{10pt}}");
        sb.AppendLine(@"    {\sffamily\small\color{greyText} " + paper.Affiliation + @" \\ \vspace{4pt}}");
        sb.AppendLine("    {\\ttfamily\\scriptsize\\color{gray} Protocol Hash: " + paper.ProtocolHash + " | Generated: " + paper.GeneratedAt + " \\\\ \\vspace{15pt}}");
        sb.AppendLine(@"    \color{lightgray}\hrule\vspace{15pt}");
        sb.AppendLine(@"    \color{black}");
        sb.AppendLine(@"\end{center}");

        sb.AppendLine(@"\noindent\colorbox{black!4}{");
        sb.AppendLine(@"\parbox{\dimexpr\linewidth-2\fboxsep\relax}{");
        sb.AppendLine(@"    \small\sffamily\bfseries\color{auBlue} ABSTRACT \\ \vspace{4pt}");
        sb.AppendLine("    \\itshape\\rmfamily\\color{black} " + Escape(paper.Abstract));
        sb.AppendLine(@"}}");
        sb.AppendLine(@"\vspace{15pt}");

        sb.AppendLine(@"\begin{multicols}{2}");

        sb.AppendLine(@"\section{Introduction}");
        sb.AppendLine(@"\subsection{Rationale}");
        sb.AppendLine(Escape(paper.Rationale));
        sb.AppendLine(@"\subsection{Objectives}");
        sb.AppendLine(Escape(paper.Objectives));

        sb.AppendLine(@"\section{Methodology}");
        foreach (var section in paper.Methods)
        {
            sb.AppendLine($"\\subsection{{{Escape(section.Heading)}}}");
            sb.AppendLine(Body(section));
        }

        sb.AppendLine(@"\end{multicols}");
        sb.AppendLine($"\\section{{{Escape(paper.DataHeading)}}}");
        sb.AppendLine(Escape(paper.DataText));
        sb.AppendLine(@"\vspace{10pt}");
        sb.Append(paper.DataLatex);

        sb.AppendLine(@"\begin{multicols}{2}");
        sb.AppendLine($"\\section{{{Escape(paper.ResultsHeading)}}}");
        sb.AppendLine(Escape(paper.ResultsText));
        foreach (var theme in paper.Themes)
        {
            sb.AppendLine($"\\subsection{{{Escape(theme.Heading)}}}");
            sb.AppendLine(Body(theme));
        }
        if (!string.IsNullOrWhiteSpace(paper.CoverageNote))
            sb.AppendLine(@"\par\smallskip\noindent\textit{" + Escape(paper.CoverageNote) + "}");
        foreach (var block in paper.ResultsLatex)
        {
            // Figures and wide tables leave the two columns and come back to them.
            sb.AppendLine(@"\end{multicols}");
            sb.Append(block);
            sb.AppendLine(@"\begin{multicols}{2}");
        }

        sb.AppendLine(@"\section{Discussion}");
        sb.AppendLine(Escape(paper.Discussion));
        foreach (var note in paper.DiscussionNotes)
        {
            sb.AppendLine($"\\subsection{{{Escape(note.Heading)}}}");
            sb.AppendLine(Body(note));
        }

        sb.AppendLine(@"\section{Administrative Declarations}");
        foreach (var declaration in paper.Declarations)
        {
            sb.AppendLine($"\\subsection{{{Escape(declaration.Heading)}}}");
            sb.AppendLine(Body(declaration));
        }
        sb.AppendLine(@"\end{multicols}");

        sb.AppendLine(@"\clearpage");
        sb.AppendLine(@"\begin{thebibliography}{99}");
        for (int i = 0; i < paper.References.Count; i++)
            sb.AppendLine($"\\bibitem{{ref{i + 1}}} {Escape(paper.References[i])}");
        sb.AppendLine(@"\end{thebibliography}");
        sb.AppendLine(@"\end{document}");
        return sb.ToString();
    }
}

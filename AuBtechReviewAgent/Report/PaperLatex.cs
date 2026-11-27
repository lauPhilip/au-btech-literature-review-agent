using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Writes a <see cref="ReviewPaper"/> as main.tex. The layout is the same for every kind of review: the title block,
/// the abstract and keywords, the introduction and method in two columns, the data section full width, the results,
/// discussion and declarations in two columns, the bibliography, and the appendix when there is one. Plain text is
/// escaped here; LaTeX slots are written as they are. Every section has a \label, and every citation is a \cite whose
/// key is in the bibliography (issue #537). Decimals in figures need the invariant culture, which the caller sets.
/// </summary>
public static partial class PaperLatex
{
    private static readonly Regex CiteCommand = new(@"\\cite\{([^}]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex LabelCommand = new(@"\\label\{([^}]*)\}", RegexOptions.CultureInvariant);

    public static string Build(ReviewPaper paper)
    {
        var sb = new StringBuilder();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        Header(sb, paper);
        Preamble(sb, paper);
        sb.AppendLine(@"\begin{document}");
        sb.AppendLine();
        FrontMatter(sb, paper);

        void Heading(string command, string heading)
        {
            string label = UniqueLabel(labels, "sec:" + Slug(heading));
            sb.AppendLine($"\\{command}{{{PaperLatex.Heading(heading)}}}\\label{{{label}}}");
            sb.AppendLine();
        }
        void Paragraph(string latex)
        {
            if (string.IsNullOrWhiteSpace(latex)) return;
            sb.AppendLine(latex.TrimEnd());
            sb.AppendLine();
        }
        void Section(PaperSection section)
        {
            Heading("subsection", section.Heading);
            Paragraph(section.Latex ?? Prose(section.Text));
        }

        sb.AppendLine(@"\begin{multicols}{2}");
        sb.AppendLine();
        Heading("section", "Introduction");
        Heading("subsection", "Rationale");
        Paragraph(Prose(paper.Rationale));
        Heading("subsection", "Objectives");
        Paragraph(Prose(paper.Objectives));
        Heading("section", "Methodology");
        foreach (var section in paper.Methods) Section(section);
        sb.AppendLine(@"\end{multicols}");
        sb.AppendLine();

        Heading("section", paper.DataHeading);
        Paragraph(LinkTables(Prose(paper.DataText)));
        sb.Append(paper.DataLatex);
        sb.AppendLine();

        int lastColumns = sb.Length;
        sb.AppendLine(@"\begin{multicols}{2}");
        sb.AppendLine();
        Heading("section", paper.ResultsHeading);
        if (!string.IsNullOrWhiteSpace(paper.ResultsOverviewHeading) && !string.IsNullOrWhiteSpace(paper.ResultsText))
            Heading("subsection", paper.ResultsOverviewHeading);
        Paragraph(Prose(paper.ResultsText));
        foreach (var theme in paper.Themes) Section(theme);
        if (!string.IsNullOrWhiteSpace(paper.CoverageNote))
            Paragraph(@"\par\smallskip\noindent\textit{" + Escape(paper.CoverageNote) + "}");
        // Figures and wide tables leave the two columns together and come back to them once, so two blocks in a row
        // leave no empty pair of columns between them (issue #684).
        if (paper.ResultsLatex.Count > 0)
        {
            sb.AppendLine(@"\end{multicols}");
            sb.AppendLine();
            foreach (var block in paper.ResultsLatex)
            {
                sb.Append(block);
                sb.AppendLine();
            }
            lastColumns = sb.Length;
            sb.AppendLine(@"\begin{multicols}{2}");
            sb.AppendLine();
        }

        Heading("section", "Discussion");
        if (!string.IsNullOrWhiteSpace(paper.DiscussionOpeningHeading) && !string.IsNullOrWhiteSpace(paper.Discussion))
            Heading("subsection", paper.DiscussionOpeningHeading);
        Paragraph(Prose(paper.Discussion));
        foreach (var note in paper.DiscussionNotes) Section(note);
        foreach (var closing in new[] { paper.Summary, paper.Conclusion })
        {
            if (closing == null) continue;
            Heading("section", closing.Heading);
            Paragraph(closing.Latex ?? Prose(closing.Text));
        }
        Heading("section", "Declarations");
        foreach (var declaration in paper.Declarations) Section(declaration);
        // The last two-column part is not balanced (multicols*): a new page follows it, and balancing columns that
        // end near the foot of a page pushed them a few points past it (an overfull \vbox).
        sb.Replace(@"\begin{multicols}{2}", @"\begin{multicols*}{2}", lastColumns, @"\begin{multicols}{2}".Length);
        sb.AppendLine(@"\end{multicols*}");
        sb.AppendLine();

        sb.AppendLine(@"\clearpage");
        sb.AppendLine($"\\begin{{thebibliography}}{{{new string('9', Math.Max(2, paper.References.Count.ToString(CultureInfo.InvariantCulture).Length))}}}");
        for (int i = 0; i < paper.References.Count; i++)
            sb.AppendLine($"\\bibitem{{ref{i + 1}}} {EscapeReference(paper.References[i])}");
        sb.AppendLine(@"\end{thebibliography}");
        if (!string.IsNullOrWhiteSpace(paper.AppendixLatex))
        {
            // The appendix starts on a new page after the references, as in a published review; its tables are A.1, A.2, ...
            sb.AppendLine();
            sb.AppendLine(@"\clearpage");
            sb.AppendLine(@"\section*{Appendix}\label{sec:appendix}");
            sb.AppendLine(@"\setcounter{table}{0}");
            sb.AppendLine(@"\renewcommand{\thetable}{A.\arabic{table}}");
            sb.AppendLine(@"\renewcommand{\theHtable}{A.\arabic{table}} % hyperref's own name, so A.1 is not the same link as 0.1");
            sb.AppendLine();
            sb.Append(paper.AppendixLatex);
        }
        sb.AppendLine();
        sb.AppendLine(@"\end{document}");
        return Resolve(sb.ToString(), paper);
    }

    private static void FrontMatter(StringBuilder sb, ReviewPaper paper)
    {
        sb.AppendLine(@"\begin{center}");
        sb.AppendLine(@"  {\sffamily\bfseries\scriptsize\color{gray} " + Escape(paper.KindLabel) + @"\par}");
        sb.AppendLine(@"  \vspace{4pt}");
        sb.AppendLine(@"  {\rmfamily\LARGE\bfseries " + Escape(paper.Title) + @"\par}");
        sb.AppendLine(@"  \vspace{8pt}");
        sb.AppendLine(@"  {\large " + Escape(ArticleInfo.Author) + @"\textsuperscript{*}\par}");
        sb.AppendLine(@"  \vspace{4pt}");
        sb.AppendLine(@"  {\sffamily\small\color{greyText} " + Escape(paper.Authors) + @"\par}");
        sb.AppendLine(@"  \vspace{4pt}");
        sb.AppendLine(@"  {\ttfamily\scriptsize\color{gray} Protocol fingerprint: " + Escape(paper.ProtocolHash) + @" \textbar{} Generated: " + Escape(paper.GeneratedAt) + @"\par}");
        sb.AppendLine(@"\end{center}");
        sb.AppendLine(@"{\footnotesize\noindent\textsuperscript{*}" + Escape(ArticleInfo.AuthorNote) + " " + Escape(ArticleInfo.NotPeerReviewed) + @"\par}");
        sb.AppendLine(@"{\color{lightgray}\hrule}");
        sb.AppendLine();
        sb.AppendLine(@"\begin{abstract}");
        sb.AppendLine(Prose(paper.Abstract));
        sb.AppendLine(@"\end{abstract}");
        if (paper.Keywords.Count > 0)
            sb.AppendLine(@"\noindent{\small\textbf{Keywords:} " + string.Join("; ", paper.Keywords.Select(Escape)) + @"\par}");
        sb.AppendLine(@"\vspace{12pt}");
        sb.AppendLine();
    }

    /// <summary>The label of a heading or caption: lower-case letters and digits joined by hyphens.</summary>
    public static string Slug(string text)
    {
        string slug = Regex.Replace((text ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "part" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    private static string UniqueLabel(HashSet<string> used, string label)
    {
        string candidate = label;
        for (int n = 2; !used.Add(candidate); n++) candidate = $"{label}-{n}";
        return candidate;
    }

    // "Table A.1" in text written in code points to the table it names.
    private static string LinkTables(string latex) =>
        Regex.Replace(latex, @"\bTable (A\.\d+)\b", @"Table~\ref{tab:$1}");

    /// <summary>
    /// The last pass over the document: each \cite of "refN" gets the N-th reference's key, and a number with no
    /// reference is printed as it is, so every \cite resolves; a \label used twice gets a number, so every one is unique.
    /// </summary>
    private static string Resolve(string tex, ReviewPaper paper)
    {
        int count = paper.References.Count;
        var keys = paper.ReferenceKeys.Count == count && paper.ReferenceKeys.Distinct(StringComparer.Ordinal).Count() == count
                   && paper.ReferenceKeys.All(k => Regex.IsMatch(k, "^[A-Za-z0-9:._-]+$")) ? paper.ReferenceKeys : null;
        tex = CiteCommand.Replace(tex, m =>
        {
            var numbers = m.Groups[1].Value.Split(',').Select(k => k.StartsWith("ref", StringComparison.Ordinal) && int.TryParse(k[3..], out int n) ? n : 0).ToList();
            var known = numbers.Where(n => n >= 1 && n <= count).ToList();
            if (known.Count == 0) return "{[}" + string.Join(", ", numbers) + "{]}";
            return @"\cite{" + string.Join(",", known.Select(n => keys?[n - 1] ?? "ref" + n)) + "}";
        });
        if (keys != null)
            tex = Regex.Replace(tex, @"\\bibitem\{ref(\d+)\}", m => @"\bibitem{" + keys[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1] + "}");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        tex = LabelCommand.Replace(tex, m => @"\label{" + UniqueLabel(seen, m.Groups[1].Value) + "}");
        // A \ref to a table or figure the paper does not have prints the name it was given ("A.3"), never "??".
        return Regex.Replace(tex, @"\\ref\{(?:tab|fig|sec):([^}]*)\}", m => seen.Contains(m.Value[5..^1]) ? m.Value : m.Groups[1].Value);
    }

    /// <summary>The keys of a BibTeX file, in the file's order.</summary>
    public static List<string> BibKeys(string bibtex) =>
        Regex.Matches(bibtex ?? "", @"^@\w+\{([^,\s]+),", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

    /// <summary>The head of a full-width table (ltablex): its caption and label, and the column titles on every page.</summary>
    public static string TableHead(string caption, string label, string titles) =>
        $"\\caption{{{caption}}}\\label{{{label}}}\\\\\n\\toprule\n{titles} \\\\\n\\midrule\n\\endfirsthead%\n\\toprule\n{titles} \\\\\n\\midrule\n\\endhead%\n";
}

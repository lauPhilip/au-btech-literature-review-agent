using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

// The top of main.tex (issue #537): a header comment that says what the file is and how it was made, then the preamble,
// grouped and commented, so a researcher who opens it in Overleaf can find what to change.
public static partial class PaperLatex
{
    /// <summary>Where Aarhus University documents its fonts for LaTeX; the fonts themselves are only shared from there.</summary>
    public const string AuFontsUrl = "https://math.medarbejdere.au.dk/latex/au-fonts-for-latex";

    private static void Header(StringBuilder sb, ReviewPaper paper)
    {
        var a = paper.Article;
        static string Comment(string text) => Metadata(text);
        sb.AppendLine("% " + new string('-', 76));
        sb.AppendLine("% main.tex: " + (a == null ? "a literature review" : $"{Comment(a.Kind)} ({Comment(a.Guideline)})") + " written by TraceableAI.");
        sb.AppendLine("% Title: " + Comment(paper.Title));
        if (a != null) sb.AppendLine($"% Run: {a.RunId}; tool: {Comment(a.ToolLine)} ({ToolVersion.RepositoryUrl}).");
        sb.AppendLine("% Generated: " + Comment(paper.GeneratedAt) + ". This file is generated; it is not written by hand.");
        sb.AppendLine("%");
        sb.AppendLine("% How it was made: the method, the counts, the figures, the tables and the reference list were");
        sb.AppendLine("% written in code from the run's files. A language model wrote the prose (title, abstract,");
        sb.AppendLine("% introduction, results and discussion), and every citation in it was checked against the source");
        sb.AppendLine("% it cites (citation-audit.json in the run's archive). All text from sources and from the model is");
        sb.AppendLine("% escaped, so none of it can run a LaTeX command.");
        sb.AppendLine("%");
        sb.AppendLine("% Compile with pdflatex, twice for the cross-references (Overleaf does this for you). Every");
        sb.AppendLine("% package is in TeX Live. Text has one sentence per line; LaTeX reads a line break as a space.");
        sb.AppendLine("% The references are typeset at the end in the tool's APA 7 form. references.bib in the archive");
        sb.AppendLine("% holds the same works under the same keys, so every \\cite keeps working if you replace the");
        sb.AppendLine("% thebibliography environment with \\bibliographystyle{...} and \\bibliography{references}.");
        sb.AppendLine("%");
        sb.AppendLine("% Fonts: TeX Gyre Termes for text, TeX Gyre Heros for sans serif and TeX Gyre Cursor for code. Where");
        sb.AppendLine("% Aarhus University's AU Passata package (AUPassata.sty) is installed, it sets the sans serif text");
        sb.AppendLine("% instead. AU Passata belongs to Aarhus University and is not shipped with this file; staff and");
        sb.AppendLine("% students can install it from " + AuFontsUrl);
        sb.AppendLine("% " + new string('-', 76));
        sb.AppendLine();
    }

    private static void Preamble(StringBuilder sb, ReviewPaper paper)
    {
        sb.AppendLine(@"\documentclass[10pt,a4paper]{article}");
        sb.AppendLine();
        sb.AppendLine("%% Encoding and fonts");
        // T1, the encoding the TeX Gyre fonts are made for: in the default OT1 encoding "<", ">" and "|" print as "¡",
        // "¿" and "—" in running text (#482).
        sb.AppendLine(@"\usepackage[T1]{fontenc}");
        sb.AppendLine(@"\usepackage[utf8]{inputenc}");
        sb.AppendLine(@"\usepackage{tgtermes}");
        sb.AppendLine(@"\IfFileExists{AUPassata.sty}{\usepackage{AUPassata}}{\usepackage{tgheros}} % AU Passata only where installed");
        sb.AppendLine(@"\usepackage{tgcursor}");
        sb.AppendLine(@"\usepackage{microtype} % fewer badly spaced lines in the narrow columns");
        sb.AppendLine();
        sb.AppendLine("%% Page layout: 20 mm on every side, the page number inside the bottom margin");
        sb.AppendLine(@"\usepackage[a4paper,margin=20mm,includefoot]{geometry}");
        sb.AppendLine(@"\usepackage{multicol}");
        sb.AppendLine(@"\usepackage{fancyhdr}");
        sb.AppendLine(@"\pagestyle{fancy}");
        sb.AppendLine(@"\fancyhf{}");
        sb.AppendLine(@"\renewcommand{\headrulewidth}{0pt}");
        sb.AppendLine(@"\fancyfoot[C]{\sffamily\scriptsize\color{greyText} Page \thepage}");
        sb.AppendLine(@"\setlength{\emergencystretch}{2em} % last resort for a line that cannot be filled");
        sb.AppendLine();
        sb.AppendLine("%% Maths");
        sb.AppendLine(@"\usepackage{amsmath,amssymb}");
        sb.AppendLine();
        sb.AppendLine("%% Tables: booktabs rules; tabularx tables that may run over pages (ltablex)");
        sb.AppendLine(@"\usepackage{booktabs}");
        sb.AppendLine(@"\usepackage{ltablex}");
        sb.AppendLine(@"\usepackage{capt-of} % a caption for a table set inside the two columns");
        sb.AppendLine(@"\newcolumntype{Y}{>{\raggedright\arraybackslash}X}");
        sb.AppendLine(@"\newcolumntype{L}[1]{>{\raggedright\arraybackslash}p{\dimexpr#1\linewidth-6pt\relax}}");
        sb.AppendLine(@"\setlength{\LTcapwidth}{\textwidth}");
        sb.AppendLine(@"\counterwithin{table}{section} % Table 4.1 is the first table of section 4");
        sb.AppendLine();
        sb.AppendLine("%% Figures, drawn with TikZ and pgfplots");
        sb.AppendLine(@"\usepackage{xcolor}");
        sb.AppendLine(@"\usepackage{float}");
        sb.AppendLine(@"\usepackage{adjustbox}");
        sb.AppendLine(@"\usepackage{pgfplots}");
        sb.AppendLine(@"\usepgfplotslibrary{statistics}");
        sb.AppendLine(@"\pgfplotsset{compat=1.18}");
        sb.AppendLine(@"\usetikzlibrary{matrix,arrows.meta,positioning}");
        sb.AppendLine(@"\definecolor{auBlue}{HTML}{003B5C}");
        sb.AppendLine(@"\definecolor{greyText}{HTML}{4B5563}");
        sb.AppendLine(@"\definecolor{customIndigo}{HTML}{312E81}");
        sb.AppendLine();
        sb.AppendLine("%% Citations, links and the PDF's metadata (hyperref comes last)");
        sb.AppendLine(@"\usepackage{cite} % sorts and ranges the numbers: [1--4]");
        sb.AppendLine(@"\usepackage{xurl} % a web address may break anywhere");
        sb.AppendLine(@"\usepackage[hidelinks]{hyperref}");
        sb.AppendLine(@"\urlstyle{same} % links in the text face, as APA sets them");
        sb.AppendLine(@"\hypersetup{");
        sb.AppendLine("  pdftitle={" + Metadata(paper.Title) + "},");
        sb.AppendLine("  pdfauthor={" + Metadata(ArticleInfo.Author) + "},");
        if (paper.Article is { } a)
        {
            sb.AppendLine("  pdfsubject={" + Metadata($"{a.Kind}, reported following {a.Guideline}; not peer reviewed") + "},");
            if (a.Keywords.Count > 0) sb.AppendLine("  pdfkeywords={" + string.Join(", ", a.Keywords.Select(Metadata)) + "},");
            sb.AppendLine("  pdfcreator={" + Metadata(a.ToolLine) + "},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }
}

using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// main.tex as proper LaTeX (issue #537): LaTeX's typography in escaped text, one sentence per line, a label on every
/// heading, \cite with the keys of references.bib, and links that work inside tables and captions.
/// </summary>
public class PaperLatexQualityTests
{
    [Theory]
    [InlineData("Pages 12-34 in 2019-2025.", "Pages 12--34 in 2019--2025.")] // a range takes an en dash
    [InlineData("Run 2026-11-25 at 10:00.", "Run 2026-11-25 at 10:00.")] // a date is not a range
    [InlineData("Agents use tools, e.g. search.", @"Agents use tools, e.g.\ search.")] // no sentence space after an abbreviation
    [InlineData("Lee et al. found it.", @"Lee et~al.\ found it.")]
    [InlineData("It uses an LLM. The tool", @"It uses an LLM\@. The tool")] // a capital before a full stop ends the sentence
    [InlineData("Written by TraceableAI. It", @"Written by TraceableAI\@. It")]
    [InlineData("Smith, J. Lee wrote", "Smith, J. Lee wrote")] // an initial is left alone
    [InlineData("The hash abc...", @"The hash abc\ldots{}")]
    [InlineData("ORCID iD 0000-0002-1825-0097", "ORCID iD 0000-{}0002-{}1825-{}0097")] // a name, not a range
    [InlineData("Supervisors help [1].", @"Supervisors help~\cite{ref1}.")]
    [InlineData("[1, 3–5] agree.", @"{\cite{ref1,ref3,ref4,ref5}} agree.")] // braced at the start: no tie wanted
    public void Escaped_text_follows_LaTeX_typography(string input, string expected)
    {
        Assert.Equal(expected, PaperLatex.Escape(input));
    }

    [Theory]
    [InlineData("https://a.org/x_y?q=1&b=2#top", @"\url{https://a.org/x_y?q=1&b=2\#top}")]
    [InlineData("https://a.org/50%25off", @"\url{https://a.org/50\%25off}")]
    [InlineData(@"https://a.org/{x}\y", @"\url{https://a.org/\%7Bx\%7D\%5Cy}")] // a brace or backslash cannot end the argument
    [InlineData("See (https://a.org/x).", @"See (\url{https://a.org/x}).")] // the closing bracket and full stop are text
    public void A_web_address_is_a_link_that_works_in_any_argument(string input, string expected)
    {
        Assert.Equal(expected, PaperLatex.Escape(input));
    }

    [Fact]
    public void Prose_puts_one_sentence_on_each_line_and_reads_the_same()
    {
        string prose = PaperLatex.Prose("Agents loop [1]. They recover, e.g. by retrying. An LLM checks them.");

        Assert.Equal("Agents loop~\\cite{ref1}.\nThey recover, e.g.\\ by retrying.\nAn LLM checks them.", prose);
        Assert.Equal(PaperLatex.Escape("Agents loop [1]. They recover, e.g. by retrying. An LLM checks them."), prose.Replace("\n", " "));
    }

    [Fact]
    public void Citations_use_the_keys_of_references_bib_and_an_unknown_number_stays_text()
    {
        var paper = new ReviewPaper
        {
            Title = "T",
            Rationale = "Known [1, 2] and unknown [7].",
            References = { "Ana (2025). A.", "Bo (2026). B." },
            ReferenceKeys = { "ana2025a", "bo2026b" },
        };

        string tex = PaperLatex.Build(paper);

        Assert.Contains(@"Known~\cite{ana2025a,bo2026b} and unknown~{[}7{]}.", tex);
        Assert.Contains(@"\bibitem{ana2025a} Ana (2025). A.", tex);
        Assert.Contains(@"\bibitem{bo2026b} Bo (2026). B.", tex);
        Assert.DoesNotContain(@"\bibitem{ref", tex);
    }

    [Fact]
    public void Every_heading_has_a_unique_label_and_the_last_columns_are_not_balanced()
    {
        var paper = new ReviewPaper
        {
            Title = "T",
            Methods = { new("Search", "One."), new("Search", "Two.") },
            Declarations = { new("Funding", "None.") },
        };

        string tex = PaperLatex.Build(paper);

        Assert.Contains(@"\subsection{Search}\label{sec:search}", tex);
        Assert.Contains(@"\subsection{Search}\label{sec:search-2}", tex);
        var labels = Regex.Matches(tex, @"\\label\{([^}]*)\}").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
        // The part with the discussion and declarations is followed by a new page, so its columns are not balanced.
        Assert.Contains(@"\begin{multicols*}{2}", tex);
        Assert.True(tex.IndexOf(@"\begin{multicols*}{2}", StringComparison.Ordinal) < tex.IndexOf(@"\section{Discussion}", StringComparison.Ordinal));
        Assert.Contains(@"\end{multicols*}", tex);
    }

    [Fact]
    public void The_preamble_says_what_the_file_is_and_never_ships_AU_Passata()
    {
        string tex = PaperLatex.Build(new ReviewPaper { Title = "T" });

        Assert.StartsWith("% AI-generated:", tex);
        Assert.Contains("% main.tex:", tex);
        Assert.Contains(@"\IfFileExists{AUPassata.sty}{\usepackage{AUPassata}}{\usepackage{tgheros}}", tex);
        Assert.Contains(PaperLatex.AuFontsUrl, tex);
        Assert.True(tex.IndexOf(@"\usepackage{xurl}", StringComparison.Ordinal) < tex.IndexOf("{hyperref}", StringComparison.Ordinal));
        Assert.DoesNotContain(".ttf", tex);
        Assert.DoesNotContain(".otf", tex);
    }
}

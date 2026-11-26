using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// How main.tex escapes text (issue #113). A paragraph keeps its closing full stop; only a line of the reference list
/// loses a trailing full stop, comma or slash, as a bibitem does.
/// </summary>
public class PaperLatexTests
{
    [Theory]
    [InlineData("The studies agree.", "The studies agree.")]
    [InlineData("The studies agree.  ", "The studies agree.")]
    [InlineData("Ends with a comma,", "Ends with a comma,")]
    [InlineData("See https://example.org/", @"See \url{https://example.org/}")] // a web address is a link
    public void Escape_keeps_the_closing_punctuation(string input, string expected)
    {
        Assert.Equal(expected, PaperLatex.Escape(input).Replace(LatexBreaks.Break, ""));
    }

    [Theory]
    // APA 7: a reference ends with a full stop, except after its DOI or address.
    [InlineData("Smith, J. (2024). A study. *Journal*.", @"Smith, J. (2024). A study.\ \textit{Journal}.")] // a word space: not a sentence's end
    [InlineData("Smith, J. (2024). A study, ", "Smith, J. (2024). A study")]
    [InlineData("https://doi.org/10.1/abc/", @"\url{https://doi.org/10.1/abc/}")]
    [InlineData("Smith, J. (2024). A study. https://doi.org/10.1/abc.", @"Smith, J. (2024). A study.\ \url{https://doi.org/10.1/abc}")]
    public void EscapeReference_keeps_the_full_stop_except_after_a_link(string input, string expected)
    {
        Assert.Equal(expected, PaperLatex.EscapeReference(input).Replace(LatexBreaks.Break, ""));
    }

    [Fact]
    public void Both_escape_the_characters_LaTeX_reserves()
    {
        const string input = "50% of A & B cost $5 in #1_x.";
        Assert.Equal(@"50\% of A \& B cost \$5 in \#1\_x.", PaperLatex.Escape(input));
        Assert.Equal(@"50\% of A \& B cost \$5 in \#1\_x.", PaperLatex.EscapeReference(input));
    }

    [Theory]
    [InlineData("A latency of 10^6 cycles.", @"A latency of $10^{6}$ cycles.")]
    [InlineData("An error of 10^-3, or 2.5^2.", @"An error of $10^{-3}$, or $2.5^{2}$.")]
    [InlineData("About 2×10^6 tokens.", @"About 2$\times10^{6}$ tokens.")]
    [InlineData("Costs $10^6 here.", @"Costs \$$10^{6}$ here.")]
    [InlineData("x^2 and 10^n keep the caret.", @"x\textasciicircum 2 and 10\textasciicircum n keep the caret.")]
    public void A_number_raised_to_a_number_is_set_as_a_power(string input, string expected)
    {
        // Issue #129: "10^6" printed as "10\textasciicircum 6" in Table 3.2 and section 4.2.
        Assert.Equal(expected, PaperLatex.Escape(input));
    }

    [Fact]
    public void Empty_text_stays_empty()
    {
        Assert.Equal(string.Empty, PaperLatex.Escape(""));
        Assert.Equal(string.Empty, PaperLatex.EscapeReference(null!));
    }

    [Theory]
    [InlineData("α-UMi [97]", @"$\alpha$-UMi~\cite{ref97}")]
    [InlineData("Cohen's κ ≥ 0.6", @"Cohen's $\kappa$ $\geq$ 0.6")]
    [InlineData("λ-calculus and Δt", @"$\lambda$-calculus and $\Delta$t")]
    [InlineData("Mäkinen, Öner, Preuß, Jiménez", "Mäkinen, Öner, Preuß, Jiménez")]
    [InlineData("Wąsik and Þór", "Wasik and Thór")]
    [InlineData("«guillemets» and „low quotes“", "``guillemets'' and ``low quotes“")]
    [InlineData("Agents 🤖 at work", "Agents  at work")]
    [InlineData("中文 title", "?? title")]
    [InlineData("zero\u200Bwidth and thin\u2009space", "zerowidth and thin space")]
    [InlineData("ά and ǫ", @"$\alpha$ and o")]
    [InlineData("x ≈ 2 – 3 … done", @"x $\approx$ 2 – 3 … done")]
    public void Characters_pdflatex_cannot_set_are_mapped_or_replaced(string input, string expected)
    {
        // Issue #475: main.tex stopped in pdflatex on "α" in a source's name.
        Assert.Equal(expected, PaperLatex.Escape(System.Text.RegularExpressions.Regex.Unescape(input)));
    }

    [Fact]
    public void Every_character_of_the_escaped_text_is_one_the_preamble_sets()
    {
        string tricky = "α β γ Ω ∑ √ ∞ ≠ ← ↔ ⇒ ✓ ★ ¹ ⁴ ₂ Ð ð Đ Ŋ Ę Į Ų ‹›‚„«» 😀 🇩🇰 한국어 русский עברית ‐ – — ‘’ “” • … € ™ ° ± µ ½ ß Æ Œ Ł ł ő ű č š ž ı ș ț";
        string escaped = PaperLatex.Escape(tricky);

        var unsafeChars = escaped.EnumerateRunes().Where(r => !LatexText.IsSafe(r)).Select(r => r.ToString()).ToList();
        Assert.Empty(unsafeChars);
    }

    [Fact]
    public void The_preamble_sets_text_in_T1_so_less_than_greater_than_and_bar_print_as_themselves()
    {
        // Issue #482: in the default OT1 encoding "<", ">" and "|" printed as "¡", "¿" and "—" in the PDF.
        string tex = PaperLatex.Build(new ReviewPaper { Title = "Agents <and> tools | a review" });
        Assert.Contains(@"\usepackage[T1]{fontenc}", tex);
        Assert.True(tex.IndexOf(@"\usepackage[T1]{fontenc}", StringComparison.Ordinal) < tex.IndexOf(@"\usepackage{tgtermes}", StringComparison.Ordinal));
        Assert.Contains("Agents <and> tools | a review", tex);
    }
}

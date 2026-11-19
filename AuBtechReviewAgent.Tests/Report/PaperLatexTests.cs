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
    [InlineData("See https://example.org/", "See https://example.org/")]
    public void Escape_keeps_the_closing_punctuation(string input, string expected)
    {
        Assert.Equal(expected, PaperLatex.Escape(input));
    }

    [Theory]
    [InlineData("Smith, J. (2024). A study. *Journal*.", @"Smith, J. (2024). A study. \textit{Journal}")]
    [InlineData("Smith, J. (2024). A study, ", "Smith, J. (2024). A study")]
    [InlineData("https://doi.org/10.1/abc/", "https://doi.org/10.1/abc")]
    public void EscapeReference_trims_a_trailing_full_stop_comma_or_slash(string input, string expected)
    {
        Assert.Equal(expected, PaperLatex.EscapeReference(input));
    }

    [Fact]
    public void Both_escape_the_characters_LaTeX_reserves()
    {
        const string input = "50% of A & B cost $5 in #1_x.";
        Assert.Equal(@"50\% of A \& B cost \$5 in \#1\_x.", PaperLatex.Escape(input));
        Assert.Equal(@"50\% of A \& B cost \$5 in \#1\_x", PaperLatex.EscapeReference(input));
    }

    [Fact]
    public void Empty_text_stays_empty()
    {
        Assert.Equal(string.Empty, PaperLatex.Escape(""));
        Assert.Equal(string.Empty, PaperLatex.EscapeReference(null!));
    }
}

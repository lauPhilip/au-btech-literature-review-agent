using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal paper keeps inside its margins: the flow diagram is shrunk to the text width when wider, and a table
/// in a column is a stack of one-row tables, so the column can break between rows instead of running off the page.
/// </summary>
public class MultivocalPaperMarginsTests
{
    [Fact]
    public void TheFlowDiagramIsShrunkToTheTextWidthWhenWider()
    {
        string data = MultivocalPaperParts.DataLatex(MultivocalPaperTests.Input()).Replace("\r\n", "\n");

        int box = data.IndexOf(@"\begin{adjustbox}{max width=\linewidth}", StringComparison.Ordinal);
        int picture = data.IndexOf(@"\begin{tikzpicture}", StringComparison.Ordinal);
        int end = data.IndexOf(@"\end{adjustbox}", StringComparison.Ordinal);
        Assert.True(box >= 0 && box < picture && picture < end, "the first tikzpicture is inside the adjustbox");
        Assert.True(end < data.IndexOf(@"\caption{Flow of sources", StringComparison.Ordinal));
    }

    [Fact]
    public void AColumnTableIsAStackOfOneRowTablesSoTheColumnCanBreakBetweenRows()
    {
        var rows = Enumerable.Range(1, 30).Select(i => (IReadOnlyList<string>)new[] { $"Value {i}", "3", string.Join(", ", Enumerable.Range(1, 40)) }).ToList();
        string latex = MultivocalPaperParts.ColumnTable(new PaperTable("Tool or framework", new[] { "Value", "Sources", "Cited" }, rows, "A note."));

        // The header, each row and the closing rule: 32 tables of the same three columns.
        Assert.Equal(32, Regex.Matches(latex, @"\\begin\{tabular\}").Count);
        Assert.Equal(32, Regex.Matches(latex, Regex.Escape(@"\begin{tabular}{@{}L{0.333}L{0.333}L{0.333}@{}}")).Count);
        Assert.Contains(@"\noindent\begin{tabular}", latex);
        Assert.Contains(@"Value 7 & 3 & 1, 2, 3", latex);
        Assert.Single(Regex.Matches(latex, @"\\toprule"));
        Assert.Single(Regex.Matches(latex, @"\\bottomrule"));
        Assert.Contains(@"\textit{A note.}", latex);
        Assert.DoesNotContain(@"\\[2pt]", latex); // the note no longer hangs off the end of one long tabular
    }
}

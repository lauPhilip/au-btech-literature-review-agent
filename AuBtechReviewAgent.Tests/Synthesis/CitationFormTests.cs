using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #725: run 34d1af9a's text had markers side by side ("[9][2][11]"), a sentence that opened with a bare marker
/// (".[9] describes") and a marker written onto a word ("economics[4]"). The prose clean-up now writes them as one
/// sorted marker, a named study and a spaced marker, and lists each change.
/// </summary>
public class CitationFormTests
{
    [Theory]
    [InlineData("Agents plan [9][2][11].", "Agents plan [2, 9, 11].")]
    [InlineData("Costs rise in economics[4].", "Costs rise in economics [4].")]
    [InlineData("Costs rose. [9] describes a remedy.", "Costs rose. Study [9] describes a remedy.")]
    [InlineData("Costs rose. [2, 9] describe a remedy.", "Costs rose. Studies [2, 9] describe a remedy.")]
    [InlineData("[9] describes a remedy.", "Study [9] describes a remedy.")]
    public void CitationsAreWrittenInOneForm(string text, string expected) =>
        Assert.Equal(expected, ProseCleaner.Clean(text, "x").Text);

    [Theory]
    [InlineData("Costs rose [9]. Prices fell [2].")]
    [InlineData("Costs rose. [9] Prices fell.")]
    [InlineData("Smith et al. [9] describe a remedy.")]
    [InlineData("Some tools, e.g. [9] shows, help.")]
    [InlineData("Study [9] describes a remedy.")]
    public void CitationsAlreadyInFormAreLeftAlone(string text) =>
        Assert.Equal(text, ProseCleaner.Clean(text, "x").Text);

    [Fact]
    public void AMultivocalTextNamesASource()
    {
        var (text, changes) = ProseCleaner.Clean("Costs rose. [9] describes a remedy.", "synthesis-1", "source");

        Assert.Equal("Costs rose. Source [9] describes a remedy.", text);
        Assert.Contains(changes, c => c.Kind == "citation that opened a sentence named" && c.Field == "synthesis-1");
    }

    [Fact]
    public void EveryChangeIsListed()
    {
        var (_, changes) = ProseCleaner.Clean("Agents plan [9][2] in economics[4].", "x");

        Assert.Contains(changes, c => c.Kind == "citations side by side merged");
        Assert.Contains(changes, c => c.Kind == "space put before a citation");
    }
}

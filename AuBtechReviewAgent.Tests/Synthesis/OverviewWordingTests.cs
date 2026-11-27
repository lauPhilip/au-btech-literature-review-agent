using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The overview paragraph written in code never opens a sentence on a numeral (issue #746, run 866a897b).</summary>
public class OverviewWordingTests
{
    private static ThematicCodebook Book(int studies, int codes, int uncoded) => new()
    {
        Codes = Enumerable.Range(1, codes).Select(i => new ThematicCode { Id = $"C{i}", Reference = 1 + i % studies, Anchor = "verified finding" }).ToList(),
        Themes = new() { new Theme { Id = "T1", Name = "Recovery", Studies = Enumerable.Range(1, studies).ToList() } },
        Uncoded = Enumerable.Range(studies + 1, uncoded).Select(r => new UncodedStudy(r, "no finding.")).ToList(),
    };

    [Fact]
    public void TheCountsAreGivenAfterAnOfClause()
    {
        string text = ThematicSynthesis.Overview(Book(12, 36, 4), 16);

        Assert.Contains("Of the 16 included studies, 12 yielded 36 codes, grouped into 1 theme: Recovery (12 studies).", text);
        Assert.Contains("Of the 16 included studies, 4 were not placed in a theme; the reasons are listed in the evidence map.", text);
        Assert.DoesNotContain("12 studies yielded", text);
        Assert.DoesNotContain(". 4 studies were not placed", text);
    }

    [Fact]
    public void OneStudyIsNamedInTheSingular()
    {
        string text = ThematicSynthesis.Overview(Book(1, 1, 0), 1);

        Assert.Contains("The included study yielded 1 code, grouped into 1 theme", text);
    }

    [Theory]
    [InlineData(1, 1, 0, 3)]
    [InlineData(2, 5, 1, 3)]
    [InlineData(12, 36, 4, 16)]
    public void NoSentenceOfTheOverviewOpensOnADigit(int studies, int codes, int uncoded, int included)
    {
        string text = ThematicSynthesis.Overview(Book(studies, codes, uncoded), included);

        Assert.DoesNotMatch(new Regex(@"(?:^|[.!?]\s+)\d"), text);
    }
}

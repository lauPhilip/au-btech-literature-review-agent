using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A venue field that holds a whole citation is cut back to the proceedings it names (issue #678, run 8942b961).</summary>
public class VenueFromCitationTests
{
    private const string Title = "Focus Agent: LLM-Powered Virtual Focus Group";

    private const string FullCitation =
        "In Taiyu Zhang, Xuesong Zhang, Robbe Cools, and Adalberto Simeone. 2024. Focus Agent: LLM-Powered Virtual Focus Group. " +
        "In ACM International Conference on Intelligent Virtual Agents (IVA '24), September 16--19, 2024, GLASGOW, United Kingdom (pp. 1-10)";

    private static AcademicPaper Paper(string venue) =>
        new("arxiv:2409.01907", Title, "abstract", "Published: 2024",
            new List<string> { "Taiyu Zhang", "Xuesong Zhang", "Robbe Cools", "Adalberto Simeone" }, venue, "10.1145/3652988.3673918", null);

    [Fact]
    public void AVenueThatHoldsAWholeCitationBecomesTheProceedingsItNames()
    {
        var result = ApaCitationBuilder.Build(Paper(FullCitation));

        Assert.Equal("ACM International Conference on Intelligent Virtual Agents (IVA '24)", result.VenueName);
        Assert.Equal("Conferences", result.VenueType);
        Assert.Equal("1–10", result.Details?.Pages);
        Assert.Contains("In *ACM International Conference on Intelligent Virtual Agents (IVA '24)* (pp. 1–10).", result.Citation);
        Assert.DoesNotContain("Taiyu Zhang", result.Citation);
        Assert.DoesNotContain("GLASGOW", result.Citation);
    }

    [Fact]
    public void AVenueWithTheCitationButNoProceedingsIsDroppedNotPrintedTwice()
    {
        var result = ApaCitationBuilder.Build(Paper("Taiyu Zhang and others. 2024. Focus Agent: LLM-Powered Virtual Focus Group. ACM"));

        Assert.Equal("", result.VenueName);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Citation, "irtual focus group"));
    }

    [Theory]
    [InlineData("Proceedings of the AAAI Symposium Series")]
    [InlineData("Focus Agent: LLM-Powered Virtual Focus Group")]
    [InlineData("Interacting with Computers")]
    public void AnOrdinaryVenueOrTheTitleAloneIsNotAWholeCitation(string venue)
    {
        Assert.False(ApaCitationBuilder.HoldsCitation(venue, Title));
    }

    [Fact]
    public void AnOrdinaryProceedingsVenueIsKeptAsItIs()
    {
        var result = ApaCitationBuilder.Build(Paper("Proceedings of the 24th ACM International Conference on Intelligent Virtual Agents"));

        Assert.Equal("Proceedings of the 24th ACM International Conference on Intelligent Virtual Agents", result.VenueName);
    }
}

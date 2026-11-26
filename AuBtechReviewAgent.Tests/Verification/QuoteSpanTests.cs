using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A quote counts as verbatim only when it is one run of the source's text (#606): not two passages stitched together,
/// not the end of one sentence run into the start of the next, while PDF text keeps matching.
/// </summary>
public class QuoteSpanTests
{
    private const string Paper =
        "1 Introduction\n\nAgent frameworks let a model call tools in a loop. Most teams add a supervisor that checks " +
        "every tool call before it runs. This adds latency but catches unsafe actions early.\n\n" +
        "2 Method\n\nWe interviewed twelve platform engineers at four companies. Each interview lasted one hour and " +
        "was transcribed in full.\n\n5 Discussion\n\nThe supervisor pattern reduced incidents in every company we studied.";

    [Fact]
    public void TwoSentencesFromDifferentSectionsSplicedTogetherAreNotVerbatim()
    {
        Assert.False(CitationSupportChecker.QuoteOccursIn(
            "Most teams add a supervisor that checks every tool call... the supervisor pattern reduced incidents in every company we studied", Paper));
        Assert.False(CitationSupportChecker.QuoteOccursIn(
            "catches unsafe actions early... we interviewed twelve platform engineers", Paper));
    }

    [Fact]
    public void ASplicedQuoteIsRejectedAlsoWhenPdfTextHasNoParagraphs()
    {
        // PDF chunks are one line of text, so only the length of the gap shows the parts are far apart.
        string flat = Paper.Replace("\n\n", " ") + " " + string.Concat(Enumerable.Repeat("Filler sentence about something else entirely. ", 10));
        string late = flat + " Teams that skipped the supervisor reported more failed deployments.";
        Assert.False(CitationSupportChecker.QuoteOccursIn(
            "Agent frameworks let a model call tools in a loop... teams that skipped the supervisor reported more failed deployments", late));
    }

    [Fact]
    public void AShortEllipsisInsideAParagraphIsStillVerbatim()
    {
        Assert.True(CitationSupportChecker.QuoteOccursIn(
            "Most teams add a supervisor that checks every tool call... This adds latency but catches unsafe actions early", Paper));
    }

    [Theory]
    [InlineData("in a loop most teams add a supervisor")]
    [InlineData("checks every tool call before it runs this adds latency")]
    [InlineData("was transcribed in full 5 Discussion The supervisor pattern reduced incidents")]
    [InlineData("at four companies; each interview lasted one hour")]
    public void AQuoteCrossingASentenceEndOrHeadingIsNotVerbatim(string quote)
    {
        Assert.False(CitationSupportChecker.QuoteOccursIn(quote, Paper));
    }

    [Theory]
    [InlineData("in a loop. Most teams add a supervisor")]
    [InlineData("checks every tool call before it runs. This adds latency")]
    [InlineData("Most teams add a supervisor that checks every tool call before it runs")]
    public void AQuoteThatKeepsTheSentenceEndIsVerbatim(string quote)
    {
        Assert.True(CitationSupportChecker.QuoteOccursIn(quote, Paper));
    }

    [Fact]
    public void PdfHyphenationLigaturesQuotesAndMarkersStillMatch()
    {
        string pdf = "Prior work [12] shows that super-\nvisors cut the num­ber of unsafe calls ( see Fig. 3 ) by a third. " +
                     "The ﬁndings held for “agentic” workﬂows, too, as Smith et al. Found in 2024.";
        Assert.True(CitationSupportChecker.QuoteOccursIn("Prior work [12] shows that supervisors cut the number of unsafe calls", pdf));
        Assert.True(CitationSupportChecker.QuoteOccursIn("cut the number of unsafe calls (see Fig. 3) by a third", pdf));
        Assert.True(CitationSupportChecker.QuoteOccursIn("cut the number of unsafe calls see Fig 3 by a third", pdf));
        Assert.True(CitationSupportChecker.QuoteOccursIn("The findings held for \"agentic\" workflows, too, as Smith et al Found", pdf));
        Assert.True(CitationSupportChecker.QuoteOccursIn("THE FINDINGS HELD FOR AGENTIC WORKFLOWS", pdf));
    }

    [Fact]
    public void AMatchPaddedWithAStretchOfOtherCharactersIsNotVerbatim()
    {
        string table = "Accuracy of the agent " + new string('-', 60) + " 92 percent on the benchmark.";
        Assert.False(CitationSupportChecker.QuoteOccursIn("accuracy of the agent 92 percent on the benchmark", table));
        Assert.True(CitationSupportChecker.QuoteOccursIn("accuracy of the agent", "Accuracy of the agent, and its cost, were measured."));
    }

    [Fact]
    public void ALaterOccurrenceIsFoundWhenTheFirstCrossesASentenceEnd()
    {
        string text = "We studied the agent. Loops were common. Later we saw that the agent loops were common in every team.";
        Assert.True(CitationSupportChecker.QuoteOccursIn("the agent loops were common", text));
    }

    [Fact]
    public void SentenceEndsSkipInitialsAndAbbreviations()
    {
        Assert.Equal(0, QuoteSpan.SentenceEnds("by J. Smith et al. Further e.g. The fig. 3 shows"));
        Assert.Equal(3, QuoteSpan.SentenceEnds("It ran. Then it stopped! “Why?” 3 tools failed."));
    }

    [Fact]
    public async Task ACitationBackedByAQuoteAcrossASentenceEndCannotBeJudged()
    {
        var chat = new FakeChatService().Returns(
            """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E2","quote":"call tools in a loop most teams add a supervisor","reason":"stated"}]}""");
        var papers = new[]
        {
            new ReferencedPaper(1, "paper1", "Paper 1", "An abstract about agents.",
                new List<DocumentChunk> { new("paper1", 1, "Agent frameworks let a model call tools in a loop. Most teams add a supervisor that checks calls.") }),
        };
        var cited = CitationSupportChecker.ExtractCitedSentences("Most agent teams add a supervisor to tool loops [1].", "discussion");

        var r = Assert.Single(await CitationSupportChecker.CheckAsync(chat, cited, papers));

        Assert.False(r.QuoteVerified);
        Assert.Equal(CitationSupportChecker.Unverifiable, r.Verdict);
    }
}

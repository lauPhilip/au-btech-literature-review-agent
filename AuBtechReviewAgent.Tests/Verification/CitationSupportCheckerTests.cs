using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class CitationSupportCheckerTests
{
    private static ReferencedPaper Paper(int n, string abstractText, params string[] chunkTexts) =>
        new(n, $"paper{n}", $"Paper {n}", abstractText, chunkTexts.Select((t, i) => new DocumentChunk($"paper{n}", i + 1, t)).ToList());

    [Fact]
    public void ExtractsCitedSentencesAndExpandsRanges()
    {
        var sentences = CitationSupportChecker.ExtractCitedSentences(
            "Agents need governance [1]. Some general framing sentence. Plugins encode conventions [2, 4]. Benchmarks overstate reliability [3-5].",
            "synthesis");

        Assert.Equal(3, sentences.Count);
        Assert.Equal(new[] { 1 }, sentences[0].References);
        Assert.Equal(new[] { 2, 4 }, sentences[1].References);
        Assert.Equal(new[] { 3, 4, 5 }, sentences[2].References);
        Assert.Equal(2, sentences[1].SentenceIndex);
    }

    [Theory]
    [InlineData("the platform separates inference from governance", "LLMoxie: The platform  separates inference from governance and apps.", true)]
    [InlineData("the platform separates inference from governance", "Something unrelated entirely here.", false)]
    [InlineData("too short", "too short", false)]
    public void QuoteMustReallyOccurInTheExcerpt(string quote, string excerpt, bool expected)
    {
        Assert.Equal(expected, CitationSupportChecker.QuoteOccursIn(quote, excerpt));
    }

    [Fact]
    public async Task SupportedVerdictWithARealQuoteIsKept()
    {
        var chat = new FakeChatService().Returns(
            """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E2","quote":"separates inference, governance and application layers","reason":"stated directly"}]}""");
        var papers = new[] { Paper(1, "An abstract about agents.", "LLMoxie separates inference, governance and application layers for scale.") };
        var cited = CitationSupportChecker.ExtractCitedSentences("LLMoxie separates inference from governance [1].", "discussion");

        var results = await CitationSupportChecker.CheckAsync(chat, cited, papers);

        var r = Assert.Single(results);
        Assert.Equal(CitationSupportChecker.Supported, r.Verdict);
        Assert.True(r.QuoteVerified);
        Assert.Equal("full text", r.EvidenceBasis);
        Assert.Equal("E2 (page 1)", r.EvidenceLocation);
    }

    [Fact]
    public async Task SupportedVerdictWithAnInventedQuoteIsDowngraded()
    {
        var chat = new FakeChatService().Returns(
            """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"this sentence does not appear anywhere in the paper","reason":"looks right"}]}""");
        var papers = new[] { Paper(1, "An abstract about agent orchestration loops and safety.") };
        var cited = CitationSupportChecker.ExtractCitedSentences("Agents are safe [1].", "synthesis");

        var r = Assert.Single(await CitationSupportChecker.CheckAsync(chat, cited, papers));

        Assert.Equal(CitationSupportChecker.Unverifiable, r.Verdict);
        Assert.False(r.QuoteVerified);
        Assert.Equal("abstract only", r.EvidenceBasis);
    }

    [Fact]
    public async Task NotSupportedNeedsNoQuote()
    {
        var chat = new FakeChatService().Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"paper is about power grids"}]}""");
        var papers = new[] { Paper(1, "Reinforcement learning for distribution networks.") };
        var cited = CitationSupportChecker.ExtractCitedSentences("It proposes a documentation framework [1].", "synthesis");

        var r = Assert.Single(await CitationSupportChecker.CheckAsync(chat, cited, papers));

        Assert.Equal(CitationSupportChecker.NotSupported, r.Verdict);
    }

    [Fact]
    public async Task PaperWithoutAnyTextIsUnverifiableWithoutCallingTheModel()
    {
        var chat = new FakeChatService(); // no scripted answers: any call would throw
        var papers = new[] { Paper(1, "") };
        var cited = CitationSupportChecker.ExtractCitedSentences("Claim [1].", "synthesis");

        var r = Assert.Single(await CitationSupportChecker.CheckAsync(chat, cited, papers));

        Assert.Equal(CitationSupportChecker.Unverifiable, r.Verdict);
        Assert.Empty(chat.Prompts);
    }

    [Fact]
    public async Task OneModelCallPerCitedReference()
    {
        var chat = new FakeChatService()
            .Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"x"},{"sentence":2,"verdict":"not_supported","reason":"y"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"z"}]}""");
        var papers = new[] { Paper(1, "Abstract one."), Paper(2, "Abstract two.") };
        var cited = CitationSupportChecker.ExtractCitedSentences("First [1]. Second [1, 2].", "synthesis");

        var results = await CitationSupportChecker.CheckAsync(chat, cited, papers);

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Equal(3, results.Count);
        var summary = CitationSupportSummary.From(results);
        Assert.Equal(3, summary.NotSupported);
        Assert.Contains("Of 3 citations checked", summary.ToSentence());
    }
}

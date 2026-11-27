using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #465: run 50e9a8e3's summary for practitioners gave counts of sources that belonged to other values of the
/// map ("as 65 sources describe", "noted in 58 sources"). Each number was in the results, so the old check passed.
/// Items now give no count of sources; one that does is sent back, and other numbers are checked as before.
/// </summary>
public class PractitionerSummaryCountTests
{
    [Theory]
    [InlineData("Apply summarization, compression, or compaction, as 65 sources describe managing token budgets [3].", "65 sources")]
    [InlineData("Expose tools and skills via progressive disclosure or MCP, noted in 58 sources [4].", "58 sources")]
    [InlineData("Judge practices with formal evaluation pipelines, recommended in 25 sources [5].", "25 sources")]
    [InlineData("Prefer MCP for tools, as noted in 12 [2].", "noted in 12")]
    [InlineData("Keep summaries short, as in 12 of the 40 posts [1].", "12 of the 40 posts")]
    [InlineData("Treat this with care: 7 grey sources say so [1].", "7 grey sources")]
    public void ACountOfSourcesIsFound(string item, string count) =>
        Assert.Equal(count, ReviewWriter.SourceCount.Match(item).Value);

    [Theory]
    [InlineData("Keep the context under 8,000 tokens [1].")]
    [InlineData("Compare GPT-4 papers with care [2].")]
    [InlineData("Since 2025 reports name the pattern, check it [1].")]
    [InlineData("Use 3 retries before giving up [2].")]
    [InlineData("Cut latency from 13.8% to 0.007% with caching [3].")]
    [InlineData("Keep a running summary of long conversations [1, 2].")]
    public void OtherNumbersAreNotCounts(string item) => Assert.False(ReviewWriter.SourceCount.IsMatch(item), item);

    [Fact]
    public async Task AnItemWithACountIsSentBackAndTheRewrittenSummaryIsKept()
    {
        var sections = new[] { new SynthesisSection { Field = "synthesis-1", Heading = "Short contexts", Text = "Two sources keep running summaries [1, 2], and 2 sources retrieve only what is needed [2]." } };
        var profile = new WritingProfile { Review = "a grey literature review", Sources = "sources" };
        var model = new FakeChatService()
            .Returns("""{"lead":"For developers building agents.","items":["Keep a running summary [1].","Retrieve only what is needed, as 2 sources describe [2].","Check the summary against the source [1, 2]."]}""")
            .Returns("""{"lead":"For developers building agents.","items":["Keep a running summary [1].","Retrieve only what is needed [2].","Check the summary against the source [1, 2]."]}""");

        var summary = await ReviewWriter.WritePractitionerSummaryAsync(model, profile, "context engineering", "Map the practices", "practitioners",
            sections, "Summaries and retrieval complement each other [1, 2].", "[1] Ana\n[2] Acme", referenceCount: 2);

        Assert.Contains("Give no count of sources in an item", model.Prompts[0]);
        Assert.Contains("item 2 gives a count of sources (\"2 sources\")", model.Prompts[1]);
        Assert.Equal("Keep a running summary [1].\nRetrieve only what is needed [2].\nCheck the summary against the source [1, 2].", summary!.Text);
    }
}

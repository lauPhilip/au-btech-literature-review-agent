using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The shared grounding service: the citation check and repair, without a review engine.</summary>
public class GroundingTests
{
    // A source that is not a paper: the text of a web page, the kind of source a multivocal review cites.
    private static ReferencedPaper WebPage(int n, string text) =>
        new(n, $"web{n}", $"Vendor documentation {n}", "", new[] { new DocumentChunk($"web{n}", 1, text) });

    private static readonly GroundingOptions Mlr = new(Parallelism: 1, SecondCheck: false, Repair: true, Review: "multivocal literature review");

    [Fact]
    public async Task ASourceThatIsNotAPaperIsCheckedTheSameWay()
    {
        var chat = new FakeChatService().Returns(
            """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"every tool call is logged with its arguments","reason":"stated directly"}]}""");
        var fields = new List<(string Field, string Text)> { ("synthesis", "The agent platform logs each tool call [1].") };

        var result = await Grounding.CheckAndRepairAsync(chat, fields, new[] { WebPage(1, "In this release every tool call is logged with its arguments.") },
            new Dictionary<int, IReadOnlyList<string>>(), 1, Mlr);

        var check = Assert.Single(result.Checks);
        Assert.Equal(CitationSupportChecker.Supported, check.Verdict);
        Assert.True(check.QuoteVerified);
        Assert.Empty(result.Repairs);
        string prompt = Assert.Single(chat.Prompts);
        Assert.Contains("You are checking citations in a multivocal literature review.", prompt);
        Assert.DoesNotContain("systematic", prompt);
    }

    [Fact]
    public async Task TheSystematicReviewsPromptIsUnchangedByDefault()
    {
        var chat = new FakeChatService().Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"x"}]}""");
        var fields = new List<(string Field, string Text)> { ("synthesis", "A claim [1].") };

        await Grounding.CheckAndRepairAsync(chat, fields, new[] { WebPage(1, "Unrelated text about something else.") },
            new Dictionary<int, IReadOnlyList<string>>(), 1, new GroundingOptions(SecondCheck: false, Repair: false));

        Assert.Contains("You are checking citations in a systematic literature review.", Assert.Single(chat.Prompts));
    }

    [Fact]
    public async Task ARepairThatAddsACitationIsRejectedAndTheTextStaysAsItWas()
    {
        var chat = new FakeChatService()
            .Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"the page is about pricing"}]}""")
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"The platform has a free tier [1, 2].","reason":"narrowed"}]}""");
        var fields = new List<(string Field, string Text)> { ("synthesis", "The platform is the safest on the market [1].") };
        var sources = new[] { WebPage(1, "The platform has a free tier for students."), WebPage(2, "Another page.") };

        var result = await Grounding.CheckAndRepairAsync(chat, fields, sources, new Dictionary<int, IReadOnlyList<string>>(), 2, Mlr);

        var repair = Assert.Single(result.Repairs);
        Assert.Equal("rejected", repair.Action);
        Assert.Equal("The platform is the safest on the market [1].", Assert.Single(result.Fields).Text);
        Assert.Equal(1, result.BeforeRepair.NotSupported);
        Assert.Contains("You are correcting citations in a multivocal literature review.", chat.Prompts[1]);
    }

    [Fact]
    public async Task ASentenceThatCannotBeSupportedIsDeletedAndLogged()
    {
        var chat = new FakeChatService()
            .Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"not on the page"}]}""")
            .Returns("""{"repairs":[{"id":1,"action":"delete","reason":"no support anywhere"}]}""");
        var fields = new List<(string Field, string Text)> { ("synthesis", "Agents never fail [1]. The rest stays.") };

        var result = await Grounding.CheckAndRepairAsync(chat, fields, new[] { WebPage(1, "Agents sometimes fail in production.") },
            new Dictionary<int, IReadOnlyList<string>>(), 1, Mlr);

        Assert.Equal("The rest stays.", Assert.Single(result.Fields).Text);
        Assert.Equal("delete", Assert.Single(result.Repairs).Action);
        Assert.Empty(result.Checks);
        Assert.Equal(1, result.BeforeRepair.NotSupported);
    }
}

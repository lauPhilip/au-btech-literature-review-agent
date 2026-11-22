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

        // The rewrite is rejected, so the citation is still not supported and the second pass removes the sentence
        // in code (issue #135); the rejected rewrite never reaches the text.
        Assert.Equal(new[] { ("rejected", 1), ("delete", 2) }, result.Repairs.Select(r => (r.Action, r.Pass)));
        Assert.Equal("The platform is the safest on the market [1].", result.Repairs[0].After);
        Assert.Equal("", Assert.Single(result.Fields).Text);
        Assert.Equal(1, Assert.Single(result.Removed).Reference);
        Assert.Equal(1, result.BeforeRepair.NotSupported);
        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("You are correcting citations in a multivocal literature review.", chat.Prompts[1]);
    }

    [Fact]
    public async Task ARewriteThatLeavesAKeptCitationWorseIsUndone()
    {
        // Issue #124, from run 959b875d: a rewrite narrowed "isolation and durable execution [1, 2, 5, ...]" to
        // "durable execution [1, 2, 5, ...]", and the papers that had backed the isolation part no longer fit.
        const string sentence = "Agents isolate failures and audit every action [1, 2].";
        var chat = new FakeChatService()
            .Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"each agent runs in its own process to isolate failures","reason":"stated"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"every tool call is logged","reason":"tool calls only"}]}""")
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log every tool call [1, 2].","reason":"narrowed to tool calls"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"not_supported","reason":"the page is about processes"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"every tool call is logged","reason":"stated"}]}""")
            .Returns("""{"repairs":[]}"""); // the second pass asks again about [2], still partly supported, and gets nothing
        var fields = new List<(string Field, string Text)> { ("synthesis", "Loops recover. " + sentence) };
        var sources = new[] { WebPage(1, "In this design each agent runs in its own process to isolate failures."), WebPage(2, "In this release every tool call is logged.") };

        var result = await Grounding.CheckAndRepairAsync(chat, fields, sources, new Dictionary<int, IReadOnlyList<string>>(), 2, Mlr);

        Assert.Equal("Loops recover. " + sentence, Assert.Single(result.Fields).Text);
        Assert.Equal(new[] { ("reverted", 1), ("none", 2) }, result.Repairs.Select(r => (r.Action, r.Pass)));
        var repair = result.Repairs[0];
        Assert.Equal(sentence, repair.After);
        Assert.Contains("[1] supported → not_supported", repair.Note);
        Assert.Equal(new[] { CitationSupportChecker.Supported, CitationSupportChecker.Partial }, result.Checks.Select(c => c.Verdict));
        Assert.All(result.Checks, c => Assert.Equal(sentence, c.Sentence));
        Assert.All(result.Checks, c => Assert.Equal(1, c.SentenceIndex));
        Assert.Equal(6, chat.Prompts.Count);
    }

    [Fact]
    public async Task ARewriteThatKeepsItsOtherCitationsIsKept()
    {
        var chat = new FakeChatService()
            .Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"each agent runs in its own process to isolate failures","reason":"stated"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"every tool call is logged","reason":"tool calls only"}]}""")
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents isolate failures and log every tool call [1, 2].","reason":"narrowed to tool calls"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"each agent runs in its own process to isolate failures","reason":"stated"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"every tool call is logged","reason":"stated"}]}""");
        var fields = new List<(string Field, string Text)> { ("synthesis", "Agents isolate failures and audit every action [1, 2].") };
        var sources = new[] { WebPage(1, "In this design each agent runs in its own process to isolate failures."), WebPage(2, "In this release every tool call is logged.") };

        var result = await Grounding.CheckAndRepairAsync(chat, fields, sources, new Dictionary<int, IReadOnlyList<string>>(), 2, Mlr);

        Assert.Equal("Agents isolate failures and log every tool call [1, 2].", Assert.Single(result.Fields).Text);
        Assert.Equal("rewrite", Assert.Single(result.Repairs).Action);
        Assert.All(result.Checks, c => Assert.Equal(CitationSupportChecker.Supported, c.Verdict));
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

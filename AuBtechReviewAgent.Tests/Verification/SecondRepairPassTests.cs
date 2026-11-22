using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The second citation repair pass (issue #135): what is still partly supported after the first repair is rewritten
/// once more and checked again; what is still not supported is removed in code, with its claim when nothing else
/// backs it. Every change is logged with Pass = 2 and every removed citation is kept on file.
/// </summary>
public class SecondRepairPassTests
{
    private const string Logs = "In this release every tool call is logged.";
    private const string LogsQuote = """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"every tool call is logged","reason":"stated"}]}""";
    private const string Partly = """{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"every tool call is logged","reason":"tool calls only"}]}""";
    private const string NotSupported = """{"results":[{"sentence":1,"verdict":"not_supported","reason":"the page is about pricing"}]}""";
    private const string NoRepair = """{"repairs":[]}""";

    private static readonly GroundingOptions Options = new(Parallelism: 1, SecondCheck: false, Repair: true);
    private static readonly Dictionary<int, IReadOnlyList<string>> NoQuotes = new();

    private static ReferencedPaper Page(int n, string text) =>
        new(n, $"web{n}", $"Vendor documentation {n}", "", new[] { new DocumentChunk($"web{n}", 1, text) });

    private static List<(string Field, string Text)> Field(string text) => new() { ("synthesis", text) };

    [Fact]
    public async Task APartlySupportedCitationIsRewrittenAgainAndCheckedAgain()
    {
        var chat = new FakeChatService()
            .Returns(Partly)
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log every tool call and most prompts [1].","reason":"narrowed"}]}""")
            .Returns(Partly)
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log every tool call [1].","reason":"narrowed again"}]}""")
            .Returns(LogsQuote);

        var result = await Grounding.CheckAndRepairAsync(chat, Field("Agents log every tool call and every prompt [1]."), new[] { Page(1, Logs) }, NoQuotes, 1, Options);

        Assert.Equal("Agents log every tool call [1].", Assert.Single(result.Fields).Text);
        Assert.Equal(new[] { ("rewrite", 1), ("rewrite", 2) }, result.Repairs.Select(r => (r.Action, r.Pass)));
        Assert.Equal("Agents log every tool call and most prompts [1].", result.Repairs[1].Before);
        var check = Assert.Single(result.Checks);
        Assert.Equal((CitationSupportChecker.Supported, "Agents log every tool call [1]."), (check.Verdict, check.Sentence));
        Assert.Empty(result.Removed);
        Assert.Equal(5, chat.Prompts.Count);
        Assert.Contains("You are correcting citations", chat.Prompts[3]);
    }

    [Fact]
    public async Task ANotSupportedCitationIsDroppedWhileAnotherCitationStays()
    {
        const string sentence = "Agents log every tool call [1, 2].";
        var chat = new FakeChatService().Returns(LogsQuote).Returns(NotSupported).Returns(NoRepair);

        var result = await Grounding.CheckAndRepairAsync(chat, Field("Loops recover. " + sentence), new[] { Page(1, Logs), Page(2, "Prices start at ten euros.") }, NoQuotes, 2, Options);

        Assert.Equal("Loops recover. Agents log every tool call [1].", Assert.Single(result.Fields).Text);
        Assert.Equal(new[] { ("none", 1), ("drop_citation", 2) }, result.Repairs.Select(r => (r.Action, r.Pass)));
        Assert.Equal(new[] { 2 }, result.Repairs[1].FlaggedReferences);
        var removed = Assert.Single(result.Removed);
        Assert.Equal(new RemovedCitation("synthesis", sentence, 2, CitationSupportChecker.NotSupported, "", "the page is about pricing", SentenceDeleted: false), removed);
        // The kept citation keeps its verdict under the new sentence text, at its place.
        var kept = Assert.Single(result.Checks);
        Assert.Equal((1, CitationSupportChecker.Supported, "Agents log every tool call [1].", 1), (kept.Reference, kept.Verdict, kept.Sentence, kept.SentenceIndex));
        Assert.Equal(3, chat.Prompts.Count); // the removal asks the model nothing
    }

    [Fact]
    public async Task ASentenceIsDeletedWhenNothingElseBacksItsClaim()
    {
        var chat = new FakeChatService()
            .Returns(NotSupported)
            .Returns(LogsQuote)
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents rarely fail [1].","reason":"narrowed"}]}""")
            .Returns(NotSupported);

        var result = await Grounding.CheckAndRepairAsync(chat, Field("Agents never fail [1]. Agents log every tool call [2]."),
            new[] { Page(1, "Prices start at ten euros."), Page(2, Logs) }, NoQuotes, 2, Options);

        Assert.Equal("Agents log every tool call [2].", Assert.Single(result.Fields).Text);
        Assert.Equal(new[] { ("rewrite", 1), ("delete", 2) }, result.Repairs.Select(r => (r.Action, r.Pass)));
        Assert.Equal(("Agents rarely fail [1].", ""), (result.Repairs[1].Before, result.Repairs[1].After));
        var removed = Assert.Single(result.Removed);
        Assert.Equal((1, "Agents rarely fail [1].", true), (removed.Reference, removed.Sentence, removed.SentenceDeleted));
        var kept = Assert.Single(result.Checks);
        Assert.Equal((2, 0), (kept.Reference, kept.SentenceIndex)); // the deleted sentence no longer counts
        Assert.Equal(4, chat.Prompts.Count);
    }

    [Fact]
    public async Task AMarkerThatReadsAsANounIsLeftAloneAndLogged()
    {
        const string sentence = "Both [1] and [2] log every tool call.";
        var chat = new FakeChatService().Returns(LogsQuote).Returns(NotSupported).Returns(NoRepair);

        var result = await Grounding.CheckAndRepairAsync(chat, Field(sentence), new[] { Page(1, Logs), Page(2, "Prices start at ten euros.") }, NoQuotes, 2, Options);

        Assert.Equal(sentence, Assert.Single(result.Fields).Text);
        var log = result.Repairs[^1];
        Assert.Equal(("not applied", 2, sentence), (log.Action, log.Pass, log.After));
        Assert.Contains("names the source", log.Note);
        Assert.Empty(result.Removed);
        Assert.Equal(new[] { CitationSupportChecker.Supported, CitationSupportChecker.NotSupported }, result.Checks.Select(c => c.Verdict));
        Assert.Equal(3, chat.Prompts.Count);
    }

    [Fact]
    public async Task NoSecondPassRunsWhenTheFirstRepairLeavesNothingFlagged()
    {
        var chat = new FakeChatService()
            .Returns(Partly)
            .Returns("""{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log every tool call [1].","reason":"narrowed"}]}""")
            .Returns(LogsQuote);

        var result = await Grounding.CheckAndRepairAsync(chat, Field("Agents log every tool call and every prompt [1]."), new[] { Page(1, Logs) }, NoQuotes, 1, Options);

        Assert.Equal(new[] { ("rewrite", 1) }, result.Repairs.Select(r => (r.Action, r.Pass)));
        Assert.Empty(result.Removed);
        Assert.Equal(3, chat.Prompts.Count);

        var clean = new FakeChatService().Returns(LogsQuote);
        var untouched = await Grounding.CheckAndRepairAsync(clean, Field("Agents log every tool call [1]."), new[] { Page(1, Logs) }, NoQuotes, 1, Options);
        Assert.Empty(untouched.Repairs);
        Assert.Empty(untouched.Removed);
        Assert.Single(clean.Prompts);
    }

    [Fact]
    public void ARepairLogWrittenBeforeTheSecondPassReadsAsTheFirstPass()
    {
        const string json = """{"Field":"synthesis","Before":"A [1].","After":"","Action":"delete","FlaggedReferences":[1],"InitialVerdicts":"[1] not_supported","Note":""}""";

        var repair = System.Text.Json.JsonSerializer.Deserialize<CitationRepair>(json)!;

        Assert.Equal(("delete", 1), (repair.Action, repair.Pass));
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Issue #132: a record without an abstract must not get a reason that describes its abstract.</summary>
public class NoAbstractScreeningTests
{
    private const string ClaimsAbstract = """{"decision":"Included","reasoning":"The abstract suggests a simulation-based evaluation.","briefSummary":"A simulation study.","confidence":"medium"}""";
    private const string TitleOnly = """{"decision":"Included","reasoning":"There is no abstract, but the title names a simulation study of agents.","briefSummary":"A study of agents, judged from its title.","confidence":"low"}""";

    private static AcademicPaper Paper(string abstractText) =>
        new("id1", "Simulating agent loops", abstractText, "2024", new() { "Senyurek" }, "Venue");

    [Theory]
    [InlineData("The abstract suggests a simulation-based evaluation.", true)]
    [InlineData("Its abstract reports a survey of 40 teachers.", true)]
    [InlineData("No abstract was available; the title suggests a study.", false)]
    [InlineData("Without an abstract, the title is about agents.", false)]
    [InlineData("The abstract is missing, so the title decides.", false)]
    [InlineData("The record lacks an abstract.", false)]
    [InlineData("The title is about agents.", false)]
    [InlineData("", false)]
    public void AReasonClaimsAnAbstractOnlyWhenItDescribesOne(string text, bool claims) =>
        Assert.Equal(claims, ScreeningAnswer.ClaimsAnAbstract(text));

    [Fact]
    public async Task TheScreenerIsToldThatThereIsNoAbstract()
    {
        var chat = new FakeChatService().Returns(TitleOnly);
        await PrismaReviewEngine.ScreenPaperAsync(chat, Paper(""), "agents", "crops");

        Assert.Contains("(none: this record has no abstract)", chat.Prompts[0]);
        Assert.Contains("THIS RECORD HAS NO ABSTRACT", chat.Prompts[0]);
    }

    [Fact]
    public async Task AReasonThatDescribesAMissingAbstractIsSentBack()
    {
        var chat = new FakeChatService().Returns(ClaimsAbstract).Returns(TitleOnly);
        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, Paper(""), "agents", "crops");

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("your reasoning or summary refers to one", chat.Prompts[1]);
        Assert.Equal("There is no abstract, but the title names a simulation study of agents.", answer.Reasoning);
    }

    [Fact]
    public async Task AReasonThatKeepsDescribingIt_IsKeptWithAWarning()
    {
        var chat = new FakeChatService().RespondsWith(_ => ClaimsAbstract);
        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, Paper("  "), "agents", "crops");

        Assert.Equal("Included", answer.Decision);
        Assert.StartsWith(PrismaReviewEngine.AbstractClaimWarning, answer.Reasoning);
        Assert.EndsWith("The abstract suggests a simulation-based evaluation.", answer.Reasoning);
    }

    [Fact]
    public async Task ARecordWithAnAbstractIsScreenedAsBefore()
    {
        var chat = new FakeChatService().Returns(ClaimsAbstract);
        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, Paper("We simulate agent loops in 40 runs."), "agents", "crops");

        Assert.Single(chat.Prompts);
        Assert.DoesNotContain("THIS RECORD HAS NO ABSTRACT", chat.Prompts[0]);
        Assert.Equal("The abstract suggests a simulation-based evaluation.", answer.Reasoning);
    }
}

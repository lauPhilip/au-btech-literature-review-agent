using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The model's tensions in a multivocal synthesis are checked in code before their descriptions go into the report
/// (audit R3), and a null in them is read as missing and checked, instead of ending the synthesis (audit C2).
/// </summary>
public class MultivocalTensionCheckTests
{
    private static ModelTensions One(string a, string b, string description) =>
        new() { Tensions = { new ModelTension { GroupA = a, GroupB = b, Description = description } } };

    [Fact]
    public void NoTensionsIsFine() => Assert.Null(ModelTensions.Problem(new ModelTensions()));

    [Fact]
    public void ATensionInOneShortSentenceIsKept() =>
        Assert.Null(ModelTensions.Problem(One("V1", "V2", "V1 keeps a summary of the chat, while V2 says a summary loses details.")));

    [Theory]
    [InlineData("", "V2", "One sentence.", "needs the ids of its two values")]
    [InlineData("V1", "V2", " ", "needs a description")]
    [InlineData("V1", "V2", "V1 keeps a summary. V2 never does.", "more than one sentence")]
    [InlineData("V1", "V2", "Most practitioners prefer V1 to V2.", "counted in code")]
    [InlineData("V1", "V2", "The majority choose V1 over V2.", "counted in code")]
    [InlineData("V1", "V2", "12 sources prefer V1 to V2.", "counted in code")]
    public void ABadTensionIsSentBack(string a, string b, string description, string problem) =>
        Assert.Contains(problem, ModelTensions.Problem(One(a, b, description)));

    [Fact]
    public void ALongDescriptionIsSentBack() =>
        Assert.Contains("too long", ModelTensions.Problem(One("V1", "V2", "V1 " + new string('a', ModelTensions.MaxDescriptionChars) + ".")));

    [Fact]
    public void AnAbbreviationIsNotASecondSentence() =>
        Assert.Null(ModelTensions.Problem(One("V1", "V2", "V1 trims old turns, e.g. tool output, while V2 keeps them all.")));

    [Theory]
    [InlineData("""{"tensions":[{"groupA":null,"groupB":"V2","description":"One sentence."}]}""")]
    [InlineData("""{"tensions":[{"groupA":"V1","groupB":"V2","description":null}]}""")]
    public async Task ANullFromTheModelIsSentBackNotACrash(string first)
    {
        // The probe of the audit: each of these ended the whole synthesis step with a NullReferenceException.
        var chat = new FakeChatService().Returns(first).Returns("""{"tensions":[{"groupA":"V1","groupB":"V2","description":"V1 keeps a summary where V2 drops it."}]}""");

        var answer = await MultivocalSynthesiser.AskTensionsAsync(chat, "RQ1", "How is context managed?", "Summaries", new List<ValueGroup>(), new List<SynthesisFinding>());

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("YOUR PREVIOUS ANSWER WAS REJECTED", chat.Prompts[1]);
        Assert.Equal("V1", Assert.Single(answer.Tensions).GroupA);
    }

    [Theory]
    [InlineData("""{"tensions":null}""")]
    [InlineData("""{"tensions":[null]}""")]
    public async Task ANullListOrEntryReadsAsNoTension(string first)
    {
        // A missing list or entry is no tension, which the check allows; no crash, and nothing is invented.
        var chat = new FakeChatService().Returns(first);

        var answer = await MultivocalSynthesiser.AskTensionsAsync(chat, "RQ1", "How is context managed?", "Summaries", new List<ValueGroup>(), new List<SynthesisFinding>());

        Assert.Single(chat.Prompts);
        Assert.Empty(answer.Tensions);
    }
}

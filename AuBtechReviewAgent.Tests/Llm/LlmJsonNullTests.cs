using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>How LlmJson.GetAsync treats a null from the model (audit C2): read as missing, and the validator in code decides; never a crash.</summary>
public class LlmJsonNullTests
{
    public sealed class Item { public string Name { get; set; } = ""; public string? Note { get; set; } }
    public sealed class Answer { public List<Item> Items { get; set; } = new(); public string? Optional { get; set; } }

    private static string Rejection(FakeChatService chat) => chat.Prompts[1][(chat.Prompts[1].IndexOf("REJECTED: ", StringComparison.Ordinal) + 10)..];

    [Fact]
    public async Task NullTextReadsAsEmptySoTheValidatorSendsItBack()
    {
        var chat = new FakeChatService().Returns("""{"items":[{"name":null}]}""").Returns("""{"items":[{"name":"A"}]}""");

        var answer = await LlmJson.GetAsync<Answer>(chat, "Answer.", null, a => a.Items.Any(i => i.Name.Trim().Length == 0) ? "every item needs a name." : null);

        Assert.Equal("A", Assert.Single(answer.Items).Name);
        Assert.StartsWith("every item needs a name.", Rejection(chat));
    }

    [Fact]
    public async Task OptionalTextStaysNull()
    {
        var chat = new FakeChatService().Returns("""{"items":[{"name":"A","note":null}],"optional":null}""");

        var answer = await LlmJson.GetAsync<Answer>(chat, "Answer.", null, _ => null, repairAttempts: 0);

        Assert.Null(answer.Optional);
        Assert.Null(answer.Items[0].Note);
    }

    [Fact]
    public async Task ANullListReadsAsMissingAndTheValidatorDecides()
    {
        var chat = new FakeChatService().Returns("""{"items":null}""").Returns("""{"items":[{"name":"A"}]}""");

        var answer = await LlmJson.GetAsync<Answer>(chat, "Answer.", null, a => a.Items.Count == 0 ? "give at least one item." : null);

        Assert.Equal("A", Assert.Single(answer.Items).Name);
        Assert.StartsWith("give at least one item.", Rejection(chat));
    }

    [Fact]
    public async Task ANullListIsAcceptedAsEmptyWhenTheValidatorAllowsIt()
    {
        var chat = new FakeChatService().Returns("""{"items":null}""");

        var answer = await LlmJson.GetAsync<Answer>(chat, "Answer.", null, _ => null, repairAttempts: 0);

        Assert.Empty(answer.Items);
    }

    [Fact]
    public async Task ANullEntryInAListIsDroppedAsMissing()
    {
        var chat = new FakeChatService().Returns("""{"items":[null,{"name":"A"},null]}""");

        var answer = await LlmJson.GetAsync<Answer>(chat, "Answer.", null, a => a.Items[0].Name.Length > 0 ? null : "name it.", repairAttempts: 0);

        Assert.Equal("A", Assert.Single(answer.Items).Name);
    }

    public sealed class Fixed { public Item[] Items { get; set; } = Array.Empty<Item>(); }

    [Fact]
    public async Task ANullEntryInAFixedArrayIsSentBack()
    {
        var chat = new FakeChatService().Returns("""{"items":[null]}""").Returns("""{"items":[{"name":"A"}]}""");

        var answer = await LlmJson.GetAsync<Fixed>(chat, "Answer.", null, _ => null);

        Assert.Equal("A", Assert.Single(answer.Items).Name);
        Assert.StartsWith("the list \"items\" has a null entry", Rejection(chat));
    }

    [Fact]
    public async Task AValidatorThatThrowsIsASendBackNotACrash()
    {
        var chat = new FakeChatService().Returns("""{"items":[]}""").Returns("""{"items":[{"name":"A"}]}""");

        var answer = await LlmJson.GetAsync<Answer>(chat, "Answer.", null, a => a.Items[0].Name.Length > 0 ? null : "name it.");

        Assert.Equal("A", answer.Items[0].Name);
        Assert.StartsWith(LlmJson.ValidatorFailedProblem, Rejection(chat));
    }

    [Fact]
    public async Task AValidatorThatKeepsThrowingEndsWithTheUsualFailure()
    {
        var chat = new FakeChatService().RespondsWith(_ => """{"items":[]}""");

        var ex = await Assert.ThrowsAsync<LlmOutputException>(() =>
            LlmJson.GetAsync<Answer>(chat, "Answer.", null, a => a.Items[0].Name));

        Assert.Contains(LlmJson.ValidatorFailedProblem, ex.Message);
    }
}

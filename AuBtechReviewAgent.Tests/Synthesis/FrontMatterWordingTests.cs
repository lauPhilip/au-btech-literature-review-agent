using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issues #746 and #747 (run 866a897b): an abstract sentence that opens on a numeral, and a rationale sentence that cites a
/// study for what this review read it from, are sent back to the model; the prompt asks for neither.
/// </summary>
public class FrontMatterWordingTests
{
    private static string Front(string abstractText, string rationale) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        { ["title"] = "Agent memory: a systematic review", ["abstract"] = abstractText, ["rationale"] = rationale, ["objectives"] = "To map agent memory." });

    private static Task<FrontMatter?> WriteAsync(FakeChatService model) => ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent memory", "Map agent memory", "Studies: 2.",
        new[] { new SynthesisSection { Field = "synthesis-1", Heading = "Memory", Text = "Agents keep a running summary [1]." } }, "", "[1] A. [2] B.", 2);

    [Fact]
    public async Task AnAbstractSentenceThatOpensOnANumeralIsSentBack()
    {
        const string rationale = "Agents keep a running summary [1].";
        var model = new FakeChatService()
            .Returns(Front("We read the studies. 2 studies were included.", rationale))
            .Returns(Front("We read the studies. Two of them were included.", rationale));

        var front = await WriteAsync(model);

        Assert.Equal("We read the studies. Two of them were included.", front!.Abstract);
        Assert.Contains("opens on a numeral", model.Prompts[1]);
        Assert.Contains("Never open a sentence with a numeral", model.Prompts[0]);
    }

    [Fact]
    public async Task ARationaleSentenceThatCitesAStudyForAbstractOnlyReadingIsSentBack()
    {
        const string fragmented = "Agent evaluations appear fragmented across domains and evidence types, with some relying on abstract-only reports and others on full-text analyses [1].";
        const string fixedText = "Agent evaluations appear fragmented across domains and evidence types [1].";
        var model = new FakeChatService()
            .Returns(Front("We read the studies.", fragmented))
            .Returns(Front("We read the studies.", fixedText));

        var front = await WriteAsync(model);

        Assert.Equal(fixedText, front!.Rationale);
        Assert.Contains("cites a source for what this review read it from", model.Prompts[1]);
        Assert.Contains("A sentence that cites a study says only what that study states", model.Prompts[0]);
    }
}

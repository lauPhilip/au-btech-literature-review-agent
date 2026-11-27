using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The language edit in plain technical English (issue #599): the prompt's rules, the retry and what is kept.</summary>
public class StylisticRefinerTests
{
    // 34 words and two citations: the kind of sentence the edit must split.
    private const string Long =
        "Crucially, it is worth noting that the agent loop frameworks that were examined in the included studies were found to recover from tool failures through supervisor checks [1] and to log every action for audit [2].";

    private const string Split =
        "The agent loop frameworks in the included studies recover from tool failures through supervisor checks [1]. They also log every action for audit [2].";

    private static string Words(int n) => "Agents " + string.Join(" ", Enumerable.Repeat("act", n - 1)) + " [1].";

    [Fact]
    public async Task ThePromptStatesTheRulesAndCallsItPlainTechnicalEnglish()
    {
        var chat = new FakeChatService().Returns(Split);
        await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", Long);

        string prompt = chat.Prompts[0];
        Assert.Contains("plain technical English, based on ASD-STE100", prompt);
        Assert.Contains("25 words or fewer", prompt);
        Assert.Contains("more than 30 words is a fault", prompt);
        Assert.Contains("at most 6 sentences", prompt);
        Assert.Contains("records were screened", prompt);
        Assert.Contains("at most 3 words in a row", prompt);
        Assert.Contains("never leave out articles", prompt);
        Assert.Contains("same key term", prompt);
        Assert.Contains("there is no list of approved words", prompt);
        Assert.DoesNotContain("compliant", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never use the first person", prompt);
        Assert.DoesNotContain("\"we screened", prompt);
    }

    [Fact]
    public async Task AnEditThatSplitsTheLongSentenceIsKeptWithItsMeasurements()
    {
        var chat = new FakeChatService().Returns(Split);
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", Long);

        Assert.Equal(Split, text);
        Assert.True(delta.Applied);
        Assert.Single(chat.Prompts);
        Assert.Equal("language-edit-v4", delta.PromptVersion);
        Assert.Equal(new PlainEnglishMeasures(1, 34, 1, 1, 34), delta.Before);
        Assert.Equal(2, delta.After!.Sentences);
        Assert.Equal(0, delta.After.ShareOver25Words);
        Assert.Empty(delta.Flags!);
    }

    [Fact]
    public async Task AnEditWithASentenceOverThirtyWordsIsSentBackOnce()
    {
        var chat = new FakeChatService().Returns(Words(32)).Returns(Words(20));
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", Words(33));

        Assert.Equal(Words(20), text);
        Assert.True(delta.Applied);
        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("YOUR FIRST EDIT HAD SENTENCES OF MORE THAN 30 WORDS", chat.Prompts[1]);
        Assert.Contains("(32 words)", chat.Prompts[1]);
        Assert.Equal("The first edit had 1 sentence over 30 words (32); it was sent back once. The second edit split them.", delta.Note);
    }

    [Fact]
    public async Task WhenTheSecondEditIsStillTooLongTheTextBeforeTheEditIsKept()
    {
        var chat = new FakeChatService().Returns(Words(32)).Returns(Words(31));
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Rationale", Words(33));

        Assert.Equal(Words(33), text);
        Assert.False(delta.Applied);
        Assert.Equal(Words(31), delta.RefinedText);
        Assert.EndsWith("The second edit still had 1 sentence over 30 words (31); the text before the edit was kept.", delta.Note);
        Assert.Contains(delta.Flags!, f => f.StartsWith("Sentence of 33 words (more than 30), as before the edit"));
    }

    [Fact]
    public async Task ASecondEditThatChangesACitationIsRefusedToo()
    {
        var chat = new FakeChatService().Returns(Words(32)).Returns(Words(28).Replace(" [1]", ""));
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", Words(33));

        Assert.Equal(Words(33), text);
        Assert.False(delta.Applied);
        Assert.Contains("Rewrite changed the inline [n] citations. The text before the edit was kept.", delta.Note);
    }

    [Fact]
    public async Task ANounClusterIsFlaggedButTheEditIsKept()
    {
        const string cluster = "The software defect prediction model accuracy improved [1].";
        var chat = new FakeChatService().Returns(cluster);
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", cluster);

        Assert.Equal(cluster, text);
        Assert.True(delta.Applied);
        Assert.Equal(new[] { "Noun cluster of 5 words: \"software defect prediction model accuracy\"." }, delta.Flags);
    }

    [Fact]
    public void ARewriteThatChangesAQuotationOrATechnicalNameIsRefused()
    {
        Assert.Equal("Rewrite changed a quotation.", StylisticRefinerUtility.CheckRewrite(
            "One guide calls it \"context rot\" [1].", "One guide calls it context decay [1]."));
        Assert.Null(StylisticRefinerUtility.CheckRewrite(
            "One guide calls it “context rot” [1].", "One guide names it \"context rot\" [1]."));
        Assert.Equal("Rewrite dropped or changed a technical name: AutoGen, LangChain.", StylisticRefinerUtility.CheckRewrite(
            "Agents built on AutoGen use LangChain [2].", "Agents built on a large model use a framework [2]."));
        Assert.Null(StylisticRefinerUtility.CheckRewrite(
            "Crucially, the PRISMA flow is followed [2].", "The review follows the PRISMA flow [2]."));
    }

    // Issue #676: the edited abstract of run 8942b961 said "We removed 0 duplicates." The author is software.
    private const string Flow = "Duplicates were removed by DOI. Five records fell outside the year range. One record could not be screened.";

    [Fact]
    public async Task ASentenceTheEditPutsInTheFirstPersonIsPutBackAsItWas()
    {
        var chat = new FakeChatService().Returns("We removed duplicates by DOI. Five records fell outside the year range. We could not screen one record.");
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", Flow);

        Assert.Equal("Duplicates were removed by DOI. Five records fell outside the year range. One record could not be screened.", text);
        Assert.True(delta.Applied);
        Assert.StartsWith("The edit brought in the first person, which a text written by software does not use; 2 sentences were put back as before the edit:", delta.Note);
    }

    [Fact]
    public async Task AFirstPersonEditThatSplitsSentencesIsRefusedWhole()
    {
        var chat = new FakeChatService().Returns("We removed duplicates. We used the DOI. Five records fell outside the year range. One record could not be screened.");
        var (text, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", Flow);

        Assert.Equal(Flow, text);
        Assert.False(delta.Applied);
        Assert.EndsWith("the text before the edit was kept.", delta.Note);
    }

    [Fact]
    public void AFirstPersonTheTextAlreadyHadAndTheCountryUsAreLeftAlone()
    {
        Assert.Equal("We read the US studies.", StylisticRefinerUtility.WithoutAddedFirstPerson("We read US studies.", "We read the US studies.", out var note));
        Assert.Null(note);
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #167: a review said four times how many studies it read in full, and called its own reading part of a study's
/// method. The writers get one rule on this, and an uncited sentence that says what the studies were read from is sent
/// back; a cited sentence is a source's own claim and is left alone.
/// </summary>
public class ReadingBasisTests
{
    [Theory]
    [InlineData("Full text was read for two studies and abstracts only for eight.")]
    [InlineData("The qualitative study, evaluated through full text, reported that users stayed.")]
    [InlineData("An expert semi-structured interview with full-text analysis showed gaps.")]
    [InlineData("Two studies were read in full, the rest from their abstracts.")]
    [InlineData("Most of the evidence comes from abstract-only records.")]
    [InlineData("Only the abstract was available for eight studies.")]
    public void AnUncitedSentenceOnWhatTheReviewReadIsFound(string sentence)
    {
        Assert.Equal(sentence, ReviewWriter.ReadingBasisSentence($"Chatbots raised quit rates [1]. {sentence} Trials were short [2]."));
    }

    [Theory]
    [InlineData("Chatbots raised quit rates in two trials [1, 2].")]
    [InlineData("Large language models screened titles and abstracts with high recall.")]
    [InlineData("The tool supports full-text screening of records.")]
    [InlineData("Screening from abstracts only missed one in ten eligible papers [3].")]
    public void SentencesFromTheLiteratureAreLeftAlone(string sentence)
    {
        Assert.Null(ReviewWriter.ReadingBasisSentence(sentence));
    }

    [Fact]
    public void TheRuleNamesTheSourcesOfEachKindOfReview()
    {
        string rule = ReviewWriter.ReadingBasisRule(WritingProfile.Systematic);
        Assert.StartsWith("What a study was read from (its full text or only its abstract) is what this review read, not part of the study's method", rule);
        Assert.Contains("do not say how many studies were read in full text or from the abstract only", rule);
    }

    [Fact]
    public async Task TheDiscussionIsSentBackWhenItSaysWhatTheStudiesWereReadFrom()
    {
        var evidence = new ReviewEvidence
        {
            Sources = new[] { 1, 2 }.Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}." }).ToList(),
            BaseFacts = "2 studies; full text was read for 0 and only the abstract for 2.",
        };
        var sections = new List<SynthesisSection> { new() { Field = "synthesis-1", Heading = "Quitting", Text = "Chatbots raised quit rates [1, 2]." } };
        var chat = new FakeChatService()
            .Returns("""{"text":"Chatbots raised quit rates [1, 2]. Full text was read for none of the studies."}""")
            .Returns("""{"text":"Chatbots raised quit rates [1, 2]."}""");

        string discussion = await ReviewWriter.WriteDiscussionAsync(chat, WritingProfile.Systematic, "chatbots", "Map chatbots", sections, evidence, 2);

        Assert.Equal("Chatbots raised quit rates [1, 2].", discussion);
        Assert.Contains("4. What a study was read from", chat.Prompts[0]);
        Assert.Contains("\"Full text was read for none of the studies.\" says what this review read the studies from", chat.Prompts[1]);
    }

    [Fact]
    public void EveryClosingPartIsHeldToTheRuleTheLimitationsToo()
    {
        const string known = "Chatbots raised quit rates [1, 2].";
        Assert.Null(ReviewWriter.ClosingProblem("The evidence is thin [1].", "Teams should try chatbots [1].", "Trials are needed [2].", "Chatbots help.", known, 2));

        string? problem = ReviewWriter.ClosingProblem("Only the abstract was read for most studies.", "Teams should try chatbots [1].", "Trials are needed [2].", "Chatbots help.", known, 2);
        Assert.Equal("limitations: \"Only the abstract was read for most studies.\" says what this review read the studies from; the limitations of this review say that once, so leave it out, and never present it as a study's method.", problem);
    }
}

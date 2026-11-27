using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Issue #747 (run 866a897b): the discussion does not cite a study for what this review read it from.</summary>
public class DiscussionProcessFactsTests
{
    [Fact]
    public void ACitedDiscussionSentenceOnAbstractOnlyReadingIsSentBack()
    {
        const string text = "Evaluations look fragmented across domains, with some relying on abstract-only reports and others on full-text analyses [1].";

        string? problem = ReviewWriter.DiscussionReadingProblem(text, Array.Empty<string>(), new ReviewEvidence());

        Assert.NotNull(problem);
        Assert.Contains("abstract only or its full text", problem);
        Assert.Contains("the discussion", problem);
    }

    [Fact]
    public void ADiscussionWithoutSuchASentencePasses()
    {
        Assert.Null(ReviewWriter.DiscussionReadingProblem("Evaluations look fragmented across domains [1].", Array.Empty<string>(), new ReviewEvidence()));
    }

    [Fact]
    public void TheDiscussionPromptCarriesTheRule()
    {
        Assert.Contains(ReviewWriter.ProcessFactsRule(WritingProfile.Systematic), ReviewWriter.ReadingNotRetellingRule(WritingProfile.Systematic));
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #703: the overclaim check sent back every "requires" and "must", also in plain descriptions of what a source
/// reports, which cost model calls on correct sentences. These words now count only in the review's own voice.
/// </summary>
public class OverclaimObligationTests
{
    private static readonly AnswerCertainty[] Moderate = { new("RQ1", "moderate"), new("RQ2", "low") };

    [Theory]
    [InlineData("One study notes that its protocol requires sign-off by two engineers.")]
    [InlineData("Study [4] describes a review board that must approve each release.")]
    [InlineData("The audit reported that every change required a ticket.")]
    [InlineData("Teams use a checklist that requires a second reviewer.")]
    [InlineData("According to the survey, operators must log each override.")]
    public void ADescriptiveRequiresOrMustIsNotSentBack(string sentence)
    {
        Assert.Null(ReviewWriter.SettledClaimVerb(sentence));
        Assert.Null(ReviewWriter.OverclaimProblem(sentence, Moderate, "the abstract"));
    }

    [Theory]
    [InlineData("Safe deployment requires human review of every agent action.", "requires")]
    [InlineData("Organisations must combine validation layers with human judgment.", "must")]
    [InlineData("These findings show that effective safety requires combining validation layers.", "show that")]
    [InlineData("A study notes gains, but safe use must include audit logs.", "must")]
    public void AClaimInTheReviewsOwnVoiceIsStillSentBack(string sentence, string verb)
    {
        Assert.Equal(verb, ReviewWriter.SettledClaimVerb(sentence));
        Assert.StartsWith($"the conclusion: \"{sentence}\" states a claim as settled (\"{verb}\")", ReviewWriter.OverclaimProblem(sentence, Moderate, "the conclusion"));
    }
}

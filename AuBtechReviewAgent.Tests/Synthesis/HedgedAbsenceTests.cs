using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #726: with no answer of high certainty, run 34d1af9a's abstract hedged a reported absence into "No evaluated
/// platform may combine local-first deployment with enterprise-grade security", which reads as a ban. The hedge rule now
/// says a source's report on its own sample is stated plainly, and code sends back "no ... may".
/// </summary>
public class HedgedAbsenceTests
{
    private static readonly AnswerCertainty[] Moderate = { new("RQ1", "moderate"), new("RQ2", "low") };

    [Theory]
    [InlineData("No evaluated platform may combine local-first deployment with enterprise-grade security.")]
    [InlineData("None of the tools may log every action.")]
    public void AnAbsenceHedgedIntoABanIsSentBack(string sentence)
    {
        Assert.StartsWith($"the abstract: \"{sentence}\" reads as a ban", ReviewWriter.HedgedAbsenceProblem(sentence, "the abstract"));
        Assert.NotNull(ReviewWriter.OverclaimProblem(sentence, Moderate, "the abstract"));
    }

    [Theory]
    [InlineData("None of the evaluated platforms combined local-first deployment with enterprise-grade security.")]
    [InlineData("There is no evidence that agents may replace testers.")]
    [InlineData("Agents may reduce effort, though no study measured cost.")]
    public void APlainAbsenceOrAnOrdinaryHedgeIsFine(string sentence) =>
        Assert.Null(ReviewWriter.HedgedAbsenceProblem(sentence, "the abstract"));

    [Fact]
    public void TheHedgeRuleSaysAReportedAbsenceIsAFact()
    {
        Assert.EndsWith(ReviewWriter.ReportedFactRule, ReviewWriter.HedgeRule(Moderate, "the abstract"));
        Assert.Contains("never write \"no ... may\"", ReviewWriter.ReportedFactRule);
        Assert.Equal("", ReviewWriter.HedgeRule(null, "the abstract"));
    }
}

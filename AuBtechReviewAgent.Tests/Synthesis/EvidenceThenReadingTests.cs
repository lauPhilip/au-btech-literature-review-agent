using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Cite findings, not readings: the audit of two real runs found that the rationale, discussion and closing prompts
/// made the model cite its own reasoning, and the check then flagged it. A finding ends with its citation; the review's
/// own reading is an uncited sentence that code checks names no study, number or design.
/// </summary>
public class EvidenceThenReadingTests
{
    private const string Facts = "13 studies; full text was read for 9 and only the abstract for 4.";

    [Theory]
    [InlineData("Together, these mechanisms suggest that safety depends on supervision as much as on the model.")]
    [InlineData("Recovery and auditing answer different parts of the same problem.")]
    [InlineData("Teams adopting agent loops should treat supervision as a design choice.")]
    [InlineData("The evidence base rests on 13 studies.")]           // the review's own count, from the facts
    [InlineData("The evidence base rests on four studies.")]         // the same, in words
    [InlineData("Agents recover from faults [1]. This makes supervision central.")]
    public void TheReviewsOwnReadingsAreAccepted(string text) => Assert.Null(ReviewWriter.UncitedAttribution(text, Facts));

    [Theory]
    [InlineData("Several studies report that supervisors restart failed workers.", "studies")]
    [InlineData("Supervisors were found to restart failed workers.", "found")]
    [InlineData("The literature points to supervision as the main safeguard.", "the literature")]
    [InlineData("Recent work demonstrates gains in recovery.", "demonstrates")]
    [InlineData("Coverage rose by 40 to 90 percent.", "40")]
    [InlineData("Most evidence comes from case studies of single teams.", "case studies")]
    [InlineData("A randomised design would settle the question.", "randomised")]
    public void AnUncitedSentenceThatReportsAFindingIsRejected(string text, string cue)
    {
        string? problem = ReviewWriter.UncitedAttribution("This makes supervision central. " + text, Facts);
        Assert.NotNull(problem);
        Assert.Contains(text, problem);
        Assert.Contains(cue, problem![problem.IndexOf("has no citation", StringComparison.Ordinal)..], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACitedFindingIsLeftToTheCitationCheck() =>
        Assert.Null(ReviewWriter.UncitedAttribution("Two studies report 40 to 90 percent more coverage [1, 2].", Facts));

    [Fact]
    public void TheLimitationsAndFutureResearchMayNameKindsOfStudy()
    {
        Assert.Null(ReviewWriter.UncitedAttribution("Longitudinal field studies of supervised agents are still missing.", Facts, designsAllowed: true));
        Assert.Null(ReviewWriter.UncitedAttribution("Most of the evidence comes from case studies.", Facts, designsAllowed: true));
        Assert.NotNull(ReviewWriter.UncitedAttribution("Studies report short follow-ups only.", Facts, designsAllowed: true));
        Assert.NotNull(ReviewWriter.UncitedAttribution("A trial of 300 developers is needed.", Facts, designsAllowed: true));
    }

    [Fact]
    public void TheRuleNamesTheWordsCodeRejects()
    {
        string rule = ReviewWriter.EvidenceThenReadingRule(WritingProfile.Systematic);
        foreach (var word in new[] { "report", "found", "show", "demonstrate", "observe", "\"studies\"", "\"the literature\"" })
            Assert.Contains(word, rule);
        Assert.Contains("Never cite a sentence for the review's own reasoning", rule);
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Checks 2, 3 and 6 of the self-check: the answers, the closing parts and the coverage of the included studies.</summary>
public class PaperSelfCheckPaperTests
{
    private const string Long = "The findings agree on the main point, and they leave the question of scale open for now and for later work.";

    private static PaperCheckInput Complete(string? answer1 = null, string discussion = "Audit logs and defect cuts go together in these studies, and the two themes point the same way for teams [1, 3].") =>
        SelfCheckPaper.Input(discussion: discussion, answer1: answer1, limitations: Long, implications: Long, future: Long, conclusion: Long);

    [Fact]
    public void ACitedAnswerPasses()
    {
        Assert.Equal(CheckOutcome.Pass, SelfCheckPaper.Check(Complete(), "answers").Outcome);
    }

    [Fact]
    public void AnAnswerThatSaysWhyItCannotAnswerIsANote()
    {
        var check = SelfCheckPaper.Check(Complete("None of the findings of the included studies bears on this question, so the review cannot answer it from its evidence."), "answers");

        Assert.Equal(CheckOutcome.Note, check.Outcome);
        Assert.Equal("RQ1", Assert.Single(check.Evidence).Part);
    }

    [Fact]
    public void AnEmptyOrUncitedAnswerIsAFault()
    {
        Assert.Equal(CheckOutcome.Fault, SelfCheckPaper.Check(Complete(" "), "answers").Outcome);
        var uncited = SelfCheckPaper.Check(Complete("Agents reduce defects."), "answers");
        Assert.Equal(CheckOutcome.Fault, uncited.Outcome);
        Assert.Contains("cites no study", uncited.Evidence[0].Detail);
    }

    [Fact]
    public void AReviewWithoutQuestionsPassesTheAnswersCheck()
    {
        var input = Complete();
        input.Questions.Clear();

        Assert.Equal(CheckOutcome.Pass, SelfCheckPaper.Check(input, "answers").Outcome);
    }

    [Fact]
    public void AllClosingPartsPresentPasses()
    {
        var check = SelfCheckPaper.Check(Complete(), "parts");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Equal("All 5 parts are there.", check.Summary);
    }

    [Fact]
    public void AMissingPartThePaperDoesNotMentionIsAFault()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(discussion: Long), "parts");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Equal(4, check.Evidence.Count);
        Assert.Contains("does not say so", check.Evidence[0].Detail);
    }

    [Fact]
    public void AMissingPartThePaperOwnsUpToIsANote()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(discussion: Long + " The limitations, implications, future research and conclusion could not be written."), "parts");

        Assert.Equal(CheckOutcome.Note, check.Outcome);
    }

    [Fact]
    public void EveryStudyCitedPasses()
    {
        var check = SelfCheckPaper.Check(Complete(), "coverage");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Equal("4 of 4 studies cited.", check.Summary);
    }

    [Fact]
    public void AnUncitedStudyIsANoteWithItsReasonAndAFaultWithout()
    {
        var input = SelfCheckPaper.Input(theme1: "Agents cut defects by 41% [1].");
        Assert.Equal(CheckOutcome.Fault, SelfCheckPaper.Check(input, "coverage").Outcome);

        input.NotCitedReasons[2] = "placed in no theme: it is about design, not safety";
        var check = SelfCheckPaper.Check(input, "coverage");
        Assert.Equal(CheckOutcome.Note, check.Outcome);
        Assert.Equal("[2]", Assert.Single(check.Evidence).Text);
        Assert.Contains("about design", check.Evidence[0].Detail);
    }

    [Fact]
    public void AThemeOnOneStudyNeedsAReason()
    {
        var input = Complete();
        input.Themes.Add(new PaperTheme("Scenarios", new[] { 3 }));
        Assert.Equal(CheckOutcome.Fault, SelfCheckPaper.Check(input, "coverage").Outcome);

        input.Themes[^1] = new PaperTheme("Scenarios", new[] { 3 }, "the only study of scenario tests");
        Assert.Equal(CheckOutcome.Note, SelfCheckPaper.Check(input, "coverage").Outcome);
    }
}

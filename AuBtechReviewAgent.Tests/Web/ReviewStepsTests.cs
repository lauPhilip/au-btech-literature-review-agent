using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The seven steps of New review while a run is going (redesign step 7), mapped from a run's own plan of six to
/// eight steps.
/// </summary>
public class ReviewStepsTests
{
    private static ReviewStats At(string step, double fraction = 0, bool chaining = true, bool review = true) => new()
    {
        ProgressPlan = RunProgress.Plan(chaining, review),
        ProgressStep = step,
        ProgressFraction = fraction,
    };

    [Fact]
    public void TheSevenStepsAreTheOnesUnderTheComposer()
    {
        Assert.Equal(ReviewSteps.Count, ReviewSteps.All.Count);
        Assert.Equal(new[] { "Protocol", "Search", "Screening", "Evidence", "Writing", "Artifact", "Checking" }, ReviewSteps.All.Select(s => s.Title));
    }

    [Theory]
    [InlineData(RunProgress.Screening, 0.2, "Screening")]
    [InlineData(RunProgress.Chaining, 0.2, "Citation chaining")]
    [InlineData(RunProgress.Synthesis, 0.5, "Writing")]
    [InlineData(RunProgress.Synthesis, 0.95, "Artifact")]
    [InlineData(RunProgress.Checking, 0.3, "Citation checks")]
    public void TheProgressLineNamesTheStepThatIsRunning(string step, double fraction, string label)
    {
        Assert.Equal(label, ReviewSteps.Label(At(step, fraction), "Writing the protocol"));
    }

    [Fact]
    public void TheProgressLineSaysWhatItDoesBeforeTheFirstStep()
    {
        Assert.Equal("Writing the protocol", ReviewSteps.Label(new ReviewStats(), "Writing the protocol"));
    }

    [Theory]
    [InlineData(RunProgress.Search, 0.5, ReviewSteps.Search)]
    [InlineData(RunProgress.Screening, 0.2, ReviewSteps.Screening)]
    [InlineData(RunProgress.Chaining, 0.2, ReviewSteps.Screening)]
    [InlineData(RunProgress.Review, 0, ReviewSteps.Screening)]
    [InlineData(RunProgress.FullText, 0.9, ReviewSteps.Evidence)]
    [InlineData(RunProgress.Extraction, 0.1, ReviewSteps.Evidence)]
    [InlineData(RunProgress.Synthesis, 0.5, ReviewSteps.Writing)]
    [InlineData(RunProgress.Synthesis, 0.95, ReviewSteps.Artifact)]
    [InlineData(RunProgress.Checking, 0.3, ReviewSteps.Checking)]
    public void EveryStepOfAPlanFallsIntoOneOfTheSeven(string step, double fraction, int expected)
    {
        Assert.Equal(expected, ReviewSteps.Current(At(step, fraction), PrismaReviewEngine.StageScreening));
        Assert.Equal(expected, ReviewSteps.Current(At(step, fraction, chaining: false, review: false), PrismaReviewEngine.StageScreening));
    }

    [Fact]
    public void TheProtocolIsCurrentBeforeTheFirstStepAndNothingRunsInTheQueue()
    {
        Assert.Equal(ReviewSteps.Protocol, ReviewSteps.Current(At(""), PrismaReviewEngine.StageScreening));
        Assert.Equal(-1, ReviewSteps.Current(At(""), PrismaReviewEngine.StageQueued));
        Assert.Equal(1, ReviewSteps.Number(-1));
        Assert.All(Enumerable.Range(0, ReviewSteps.Count), i => Assert.Equal("pending", ReviewSteps.StateOf(i, -1)));
    }

    [Fact]
    public void AFinishedRunHasEveryStepDone()
    {
        Assert.Equal(ReviewSteps.Count, ReviewSteps.Current(At(RunProgress.Done), PrismaReviewEngine.StageSynthesizing));
        Assert.Equal(ReviewSteps.Count, ReviewSteps.Current(At(RunProgress.Checking, 0.4), PrismaReviewEngine.StageComplete));
        Assert.Equal(ReviewSteps.Count, ReviewSteps.Number(ReviewSteps.Count));
        Assert.All(Enumerable.Range(0, ReviewSteps.Count), i => Assert.Equal("done", ReviewSteps.StateOf(i, ReviewSteps.Count)));
    }

    [Fact]
    public void StepsBeforeTheCurrentOneAreDoneAndTheOnesAfterWait()
    {
        int current = ReviewSteps.Current(At(RunProgress.Extraction, 0.5), PrismaReviewEngine.StageScreening);
        Assert.Equal(new[] { "done", "done", "done", "current", "pending", "pending", "pending" },
            Enumerable.Range(0, ReviewSteps.Count).Select(i => ReviewSteps.StateOf(i, current)));
        Assert.Equal(4, ReviewSteps.Number(current));
    }

    [Fact]
    public void SearchNamesHowManySourcesTheRunAsked()
    {
        Assert.Equal("3 sources", ReviewSteps.Line(ReviewSteps.Search, 3));
        Assert.Equal("1 source", ReviewSteps.Line(ReviewSteps.Search, 1));
        Assert.Equal(ReviewSteps.All[ReviewSteps.Search].Line, ReviewSteps.Line(ReviewSteps.Search, 0));
        Assert.Equal("Every record twice", ReviewSteps.Line(ReviewSteps.Screening, 3));
    }
}

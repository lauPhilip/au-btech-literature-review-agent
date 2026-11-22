using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The parts of the multivocal runner that need no run: how the calls of a new start are numbered on in the call log,
/// and the progress shown from the stage and the progress line.
/// </summary>
public class MultivocalProgressAndLogTests
{
    [Fact]
    public void TheCallsOfAStartAreNumberedOnFromTheCallsAlreadyInTheLog()
    {
        var earlier = new List<LlmCallRecord> { new() { Sequence = 1, Stage = "screening" }, new() { Sequence = 2, Stage = "screening" } };
        var now = new List<LlmCallRecord> { new() { Sequence = 2, Stage = "map" }, new() { Sequence = 1, Stage = "map", PromptSha256 = "a" } };

        var merged = MultivocalRunLog.Merge(earlier, now);

        Assert.Equal(new[] { 1, 2, 3, 4 }, merged.Select(c => c.Sequence));
        Assert.Equal("a", merged[2].PromptSha256); // in the order they were made
        Assert.Equal(2, now[0].Sequence); // the recorder's own records are left as they are
        Assert.Empty(MultivocalRunLog.Merge(new List<LlmCallRecord>(), new List<LlmCallRecord>()));
    }

    [Fact]
    public void TheProgressFollowsTheStageAndTheProgressLine()
    {
        Assert.Equal(0, MultivocalProgress.Percent(MultivocalPlanner.StagePlanned, null));
        Assert.Equal(4, MultivocalProgress.Percent(MultivocalSearcher.StageSearching, "4 of 5 searches · Fake: \"context\"")); // four fifths of the 5 of the search
        Assert.Equal(5 + 20 + 25, MultivocalProgress.Percent(MultivocalQualityAssessor.StageAssessed, null));
        Assert.Equal(5 + 20 + 25 + 5 + 9, MultivocalProgress.Percent(MultivocalExtractor.StageExtracting, "18 of 30 sources extracted"));
        Assert.Equal(80, MultivocalProgress.Percent(MultivocalSynthesiser.StageSynthesised, null)); // writing and checking are left
        Assert.Equal(90 + 5, MultivocalProgress.Percent(MultivocalWriter.StageChecking, "First check: 5 of 10 sources"));
        Assert.Equal(100, MultivocalProgress.Percent(MultivocalWriter.StageChecked, null));
        Assert.Equal(0, MultivocalProgress.Fraction("RQ1: findings in 4 values; naming the themes"));
        Assert.Equal(new[] { "done", "done", "current", "waiting", "waiting", "waiting", "waiting", "waiting" },
            Enumerable.Range(0, MultivocalProgress.Steps.Count).Select(i => MultivocalProgress.StateOf(i, MultivocalQualityAssessor.StageAssessing)));
    }
}

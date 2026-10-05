using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class ScreeningReviewTests
{
    private static ReviewState StateWith(params ScreeningLog[] logs)
    {
        var state = new ReviewState();
        state.Phases.Screening.AddRange(logs);
        state.Stats.Screened = logs.Length;
        state.Stats.Included = logs.Count(l => l.Decision == "Included");
        state.Stats.Excluded = logs.Count(l => l.Decision == "Excluded" && l.BriefSummary != "N/A");
        state.Stats.FailedPeerReviewCheck = logs.Count(l => l.BriefSummary == "N/A");
        return state;
    }

    private static ScreeningLog Log(string id, string decision, string summary = "summary", string reasoning = "model reasoning") =>
        new(id, $"Title {id}", decision, reasoning, $"Citation {id}", summary);

    [Fact]
    public void OverridesAreRecordedWithTheModelDecisionAndCountersFollow()
    {
        var state = StateWith(Log("a", "Included"), Log("b", "Excluded"), Log("c", "Excluded"));

        PrismaReviewEngine.ApplyScreeningReview(state, new[]
        {
            new ScreeningOverride("a", "Included", null),         // confirmed
            new ScreeningOverride("b", "Included", "Relevant after all"), // changed
        });

        Assert.Equal(2, state.Stats.HumanReviewed);
        Assert.Equal(1, state.Stats.HumanOverrides);
        Assert.Equal(2, state.Stats.Included);
        Assert.Equal(1, state.Stats.Excluded);

        var b = state.Phases.Screening.Single(l => l.PaperId == "b");
        Assert.Equal("Included", b.Decision);
        Assert.Equal("Excluded", b.ModelDecision);
        Assert.True(b.HumanReviewed);
        Assert.Equal("Relevant after all", b.HumanNote);

        var a = state.Phases.Screening.Single(l => l.PaperId == "a");
        Assert.True(a.HumanReviewed);
        Assert.Null(a.ModelDecision);

        var c = state.Phases.Screening.Single(l => l.PaperId == "c");
        Assert.False(c.HumanReviewed);
    }

    [Fact]
    public void IncludingAPeerReviewFilteredRecordMovesItOutOfThatCounter()
    {
        var filtered = Log("p", "Excluded", summary: "N/A",
            reasoning: "Excluded during post-retrieval pre-screening: Document was classified as an un-reviewed preprint.");
        var state = StateWith(filtered);

        PrismaReviewEngine.ApplyScreeningReview(state, new[] { new ScreeningOverride("p", "Included", null) });

        Assert.Equal(0, state.Stats.FailedPeerReviewCheck);
        Assert.Equal(1, state.Stats.Included);
        Assert.Equal(0, state.Stats.Excluded);
        Assert.Contains("human reviewer", state.Phases.Screening[0].BriefSummary);
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #473: the paper said only "The reviewer looked at 0 decisions", never that 97 decisions had been flagged for
/// a look, and the run metrics had UncertainDecisions = 0. The method now says how many were flagged and why, how many
/// were looked at, and how many flagged exclusions stand unseen; the metrics count both.
/// </summary>
public class MultivocalFlaggedDecisionsTests
{
    private static ScreeningAnswer Says(bool included) => new() { Decision = included ? "Included" : "Excluded", Reasoning = "r", Confidence = "high" };

    private static MultivocalScreeningFile Screening() => new()
    {
        Sources =
        {
            // Disagreed: kept and flagged.
            new GreyScreening { Address = "a", Decision = "Included", Uncertain = true, First = Says(true), Second = Says(false) },
            new GreyScreening { Address = "b", Decision = "Included", Uncertain = true, First = Says(false), Second = Says(true), Confidence = "low" },
            // Text like instructions, with low confidence too: counted once, by its first reason.
            new GreyScreening { Address = "c", Decision = "Excluded", Uncertain = true, First = Says(false), Confidence = "low", InjectionFlags = new() { "ignore-instructions" } },
            // Unsure: two exclusions, one of them looked at and changed.
            new GreyScreening { Address = "d", Decision = "Excluded", Uncertain = true, First = Says(false), Confidence = "low" },
            new GreyScreening { Address = "e", Decision = "Included", Uncertain = true, First = Says(false), Confidence = "low", Reviewed = true, ModelDecision = "Excluded" },
            // A failed call.
            new GreyScreening { Address = "f", Decision = "Error", Uncertain = true },
            // Not flagged.
            new GreyScreening { Address = "g", Decision = "Included", First = Says(true), Confidence = "high" },
        },
    };

    [Fact]
    public void TheMethodSaysHowManyWereFlaggedWhyAndHowManyStandUnseen()
    {
        string text = MultivocalScreeningText.Flagged(Screening());

        Assert.StartsWith("6 decisions were flagged for the reviewer: 2 because the two screenings disagreed, so the source was kept; " +
            "1 because the source's text looked like instructions to the model; 2 because the model was unsure (low confidence, or no summary to judge by); " +
            "1 because the model call failed, so the source was not screened. ", text);
        Assert.Contains("The reviewer looked at 1 decision and changed 1; 5 of the flagged decisions were not looked at, 2 of them exclusions that stand as the model made them; ", text);
        Assert.EndsWith("the model's decision stays on file next to the reviewer's. ", text);
    }

    [Fact]
    public void WithNothingFlaggedTheMethodSaysSo()
    {
        var s = new MultivocalScreeningFile { Sources = { new GreyScreening { Address = "g", Decision = "Included", First = Says(true) } } };
        Assert.Equal("No decision was flagged for the reviewer. The reviewer looked at 0 decisions and changed 0; the model's decision stays on file next to the reviewer's. ",
            MultivocalScreeningText.Flagged(s));
    }

    [Fact]
    public void WhenEveryFlaggedDecisionWasLookedAtNoneStandUnseen()
    {
        var s = new MultivocalScreeningFile { Sources = { new GreyScreening { Address = "d", Decision = "Excluded", Uncertain = true, Confidence = "low", Reviewed = true, ModelDecision = "Excluded" } } };
        Assert.Contains("1 decision was flagged for the reviewer: 1 because the model was unsure", MultivocalScreeningText.Flagged(s));
        Assert.Contains("The reviewer looked at 1 decision and changed 0; every flagged decision was looked at; ", MultivocalScreeningText.Flagged(s));
    }

    [Fact]
    public void TheReportMethodAndTheMetricsCountTheFlaggedDecisions()
    {
        var input = MultivocalPaperTests.Input();
        input.Screening.Sources.Clear();
        input.Screening.Sources.AddRange(Screening().Sources);

        string method = MultivocalReporter.Write(input).ToMarkdown();
        Assert.Contains("6 decisions were flagged for the reviewer", method);
        Assert.Contains("5 of the flagged decisions were not looked at, 2 of them exclusions", method);

        var m = MultivocalRunLog.BuildMetrics(input, new MultivocalPaperFile(), null, new List<LlmCallRecord>());
        Assert.Equal(6, m.UncertainDecisions);
        Assert.Equal(5, m.FlaggedNotLookedAt);
    }

    [Fact]
    public void ASystematicMetricsLineDoesNotGainTheField()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new RunMetrics { UncertainDecisions = 3 });
        Assert.DoesNotContain(nameof(RunMetrics.FlaggedNotLookedAt), json);
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Checks 1 and 5 of the self-check on the faults of run 8942b961 (issue #673): a count of records that could not be
/// screened, and result figures repeated between themes or in two places after the results.
/// </summary>
public class PaperSelfCheckFiguresTests
{
    private const string Reuse = "Plan reuse reached a 93% reuse rate with F1 0.9718 and accuracy 0.9459, and latency fell by 93.12% [1].";

    private static PaperCheckInput Paper(string theme1, string theme2, string discussion = "", string answer = "Agents help [1].", string limitations = "")
    {
        var input = SelfCheckPaper.Input(discussion: discussion, answer1: answer, theme1: theme1, limitations: limitations);
        input.Parts[2] = new PaperPart("synthesis-2", "Theme: Plan reuse", PaperPartKind.Theme, theme2);
        return input;
    }

    [Fact]
    public void ACountOfRecordsThatCouldNotBeScreenedIsOneOfTheRunsCounts()
    {
        var state = new ReviewState();
        state.Stats.TotalIdentified = 24;
        state.Stats.Screened = 18;
        state.Stats.ScreeningErrors = 1;
        var input = SelfCheckPaper.Input("We could not screen 1 record. In all, 18 records were screened. One record could not be screened.");
        var withFacts = new PaperCheckInput { Parts = input.Parts, Facts = SystematicSelfCheck.Facts(state), Questions = input.Questions, Themes = input.Themes, Included = input.Included };

        var check = PaperSelfCheck.Run(withFacts).Checks.Single(c => c.Id == "numbers");

        Assert.Empty(check.Evidence);
        Assert.Equal(CheckOutcome.Pass, check.Outcome);
    }

    [Fact]
    public void TheScreenedCountStillMeansScreenedAndANumberOnTheWrongCountIsStillAFault()
    {
        var state = new ReviewState();
        state.Stats.Screened = 18;
        state.Stats.ScreeningErrors = 1;
        var input = SelfCheckPaper.Input("We screened 1 record.");
        var withFacts = new PaperCheckInput { Parts = input.Parts, Facts = SystematicSelfCheck.Facts(state), Questions = input.Questions, Themes = input.Themes, Included = input.Included };

        var check = PaperSelfCheck.Run(withFacts).Checks.Single(c => c.Id == "numbers");

        var e = Assert.Single(check.Evidence);
        Assert.Contains("run counts 18 records screened", e.Detail);
    }

    [Fact]
    public void AFigureGivenInTwoThemesIsAFaultAndNamesBothThemes()
    {
        var input = Paper("Latency fell by 93.12% in one study [1].", Reuse);
        input.Parts[1] = new PaperPart("synthesis-1", "Theme: Latency", PaperPartKind.Theme, Reuse.Replace("Plan reuse reached", "Caching reached"));

        var check = PaperSelfCheck.Run(input).Checks.Single(c => c.Id == "figures");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Contains(check.Evidence, e => e.Part == "Theme: Plan reuse" && e.Detail.Contains("Theme: Latency"));
        Assert.Contains(check.Evidence, e => e.Detail.StartsWith("0.9718 ", StringComparison.Ordinal));
    }

    [Fact]
    public void AFigureInOneThemeAndTheAbstractAndOnePlaceAfterItStillPasses()
    {
        var input = Paper("Latency fell by 93.12% [1].", "Audit logs help [3].", discussion: "", answer: "Latency fell by 93.12% [1].");

        Assert.Equal(CheckOutcome.Pass, PaperSelfCheck.Run(input).Checks.Single(c => c.Id == "figures").Outcome);
    }

    [Fact]
    public void LatencyFiguresInTheThemeTheDiscussionAndAnAnswerAreFound()
    {
        var input = Paper("Latency was 60.937 s without reuse and 29.353 s with it [1].", "Audit logs help [3].",
            discussion: "Latency fell from 60.937 s to 29.353 s [1].", answer: "Latency fell from 60.937 s to 29.353 s [1].");
        input.Facts.Add(new PaperFact("research questions", 2, "questions?"));

        var check = PaperSelfCheck.Run(input).Checks.Single(c => c.Id == "figures");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Contains(check.Evidence, e => e.Detail.StartsWith("29.353 ", StringComparison.Ordinal));
        Assert.Contains(check.Evidence, e => e.Detail.StartsWith("60.937 ", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelNameWithAnF1ScoreIsAFigureNotAName()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(theme1: "The F1 0.9718 was best [1].", answer1: "F1 0.9718 [1].", discussion: "The F1 0.9718 shows it [1]."), "figures");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Contains("0.9718", check.Evidence[0].Detail);
    }

    [Fact]
    public void EveryCheckExplainsItselfWhateverItsOutcome()
    {
        var file = PaperSelfCheck.Run(SelfCheckPaper.Input("Defects fell by 41%."));

        Assert.All(file.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Explanation), c.Id));
    }
}

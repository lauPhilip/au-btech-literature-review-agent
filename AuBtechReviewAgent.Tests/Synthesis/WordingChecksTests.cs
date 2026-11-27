using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Issues #746 and #747 (run 866a897b): the wording checks on model text, with the sentences from the issues.</summary>
public class WordingChecksTests
{
    private static readonly WritingProfile Profile = WritingProfile.Systematic;

    [Fact]
    public void ASentenceThatOpensOnANumeralIsSentBack()
    {
        Assert.NotNull(ReviewWriter.NumeralOpeningProblem("Agents recover. 12 studies yielded 36 codes.", "the abstract"));
        Assert.NotNull(ReviewWriter.NumeralOpeningProblem("2 records were removed for publication year.", "the abstract"));
        Assert.Null(ReviewWriter.NumeralOpeningProblem("Of the 16 studies, 12 yielded 36 codes. Two records were removed for publication year (2 in all).", "the abstract"));
        Assert.Null(ReviewWriter.NumeralOpeningProblem("Smith et al. 2020 report 12 cases. See Fig. 3 for 4 of them.", "the abstract"));
        Assert.Null(ReviewWriter.NumeralOpeningProblem("", "the abstract"));
    }

    [Fact]
    public void ASentenceWhoseOnlyVerbIsReportedIsSentBack()
    {
        string empty = "Hardware-in-the-loop test automation is reported [devgan2025agentic], and psychological behavioral evaluation is reported [zhang2024psysafe].";

        Assert.NotNull(ReviewWriter.EmptyReportedProblem("Agents help [1]. " + empty, "the subsection"));
        Assert.NotNull(ReviewWriter.EmptyReportedProblem("Latency is described [2].", "the subsection"));
        Assert.NotNull(ReviewWriter.EmptyReportedProblem("Cost and quality are reported [3].", "the subsection"));
    }

    [Fact]
    public void AReportedFindingIsNotSentBack()
    {
        Assert.Null(ReviewWriter.EmptyReportedProblem("A 38 % drop in defects is reported [1].", "the subsection"));
        Assert.Null(ReviewWriter.EmptyReportedProblem("Latency is reported to fall by a third [2], and cost is described as stable [3].", "the subsection"));
        Assert.Null(ReviewWriter.EmptyReportedProblem("The loop reduces cost [1], and quality is reported [2].", "the subsection"));
    }

    [Fact]
    public void AnotherThemeNamedWithItsCapitalsIsSentBack()
    {
        var others = new[] { "Architectural and Agent-Level Safety Mechanisms", "Cost" };
        string text = "The prioritization of defenses such as RTW-A within Architectural and Agent-Level Safety Mechanisms is open [4].";

        string? problem = ReviewWriter.OtherThemeCaseProblem(text, others);

        Assert.NotNull(problem);
        Assert.Contains("architectural and agent-level safety mechanisms", problem);
        Assert.Null(ReviewWriter.OtherThemeCaseProblem("As reported under architectural and agent-level safety mechanisms [4].", others));
        Assert.Null(ReviewWriter.OtherThemeCaseProblem("Cost is reported [4].", others));
        Assert.NotNull(ReviewWriter.OtherThemeCaseProblem("The theme on Cost is reported [4].", others));
    }

    [Fact]
    public void AThemeNameIsLoweredAsAWholeInRunningText()
    {
        Assert.Equal("architectural and agent-level safety mechanisms", ReviewWriter.ThemeNameInText("Architectural and Agent-Level Safety Mechanisms"));
        Assert.Equal("defences such as RTW-A", ReviewWriter.ThemeNameInText("Defences such as RTW-A"));
        Assert.Equal("LLM agents and GPT-4", ReviewWriter.ThemeNameInText("LLM Agents and GPT-4"));
    }

    [Theory]
    [InlineData("these evaluations appear fragmented across domains and evidence types, with some relying on abstract-only reports and others on full-text analyses [2025fault].")]
    [InlineData("Some studies were read in full text [3].")]
    [InlineData("Only the abstract is available for one study [3].")]
    public void ACitedSentenceOnWhatTheReviewReadIsSentBack(string sentence)
    {
        Assert.NotNull(ReviewWriter.CitedReadingBasisProblem("Agents fail in many ways [1]. " + sentence, "the rationale"));
    }

    [Fact]
    public void ASentenceWithoutACitationOrWithoutTheWordsIsLeftAlone()
    {
        Assert.Null(ReviewWriter.CitedReadingBasisProblem("This review read some studies in full text.", "the discussion"));
        Assert.Null(ReviewWriter.CitedReadingBasisProblem("Evaluations are fragmented across domains [1].", "the discussion"));
    }

    [Fact]
    public void ThePromptRulesNameTheirChecks()
    {
        Assert.Contains("numeral", ReviewWriter.WordingRule(Profile));
        Assert.Contains("is reported", ReviewWriter.WordingRule(Profile));
        Assert.Contains("lower case", ReviewWriter.WordingRule(Profile));
        Assert.Contains("abstract only", ReviewWriter.ProcessFactsRule(Profile));
    }
}

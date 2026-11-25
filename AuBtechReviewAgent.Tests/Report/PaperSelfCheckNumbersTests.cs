using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A hand-built paper for the self-check tests: four included studies, two themes, two questions.</summary>
public static class SelfCheckPaper
{
    public static readonly List<PaperFact> Facts = new()
    {
        new("records identified", 40, "identif", "found", "retriev"),
        new("duplicates removed", 6, "duplicat"),
        new("records screened", 34, "screen"),
        new("studies included", 4, "includ", "retain"),
        new("records excluded", 30, "exclud"),
        new("themes", 2, "themes?"),
    };

    public static PaperCheckInput Input(string abstractText = "", string conclusion = "", string discussion = "", string? answer1 = null,
        string future = "", string implications = "", string theme1 = "Agents cut defects by 41% [1]. A second study agrees [2].", string limitations = "")
    {
        var parts = new List<PaperPart>
        {
            new("abstractItem", "Abstract", PaperPartKind.Abstract, abstractText),
            new("synthesis-1", "Theme: Defect reduction", PaperPartKind.Theme, theme1),
            new("synthesis-2", "Theme: Governance", PaperPartKind.Theme, "Audit logs support governance [3, 4]."),
            new("discussionItem", "Discussion", PaperPartKind.Discussion, discussion),
            new("researchAnswer1", "Answer to RQ1", PaperPartKind.Answer, answer1 ?? "Agents reduce defects [1]."),
            new("evidenceLimitationsItem", "Limitations of the evidence", PaperPartKind.Limitations, limitations),
            new("implicationsItem", "Implications", PaperPartKind.Implications, implications),
            new("futureResearchItem", "Future research", PaperPartKind.FutureResearch, future),
            new("conclusionItem", "Conclusion", PaperPartKind.Conclusion, conclusion),
        };
        return new PaperCheckInput
        {
            Parts = parts,
            Facts = Facts,
            Themes = new() { new("Defect reduction", new[] { 1, 2 }), new("Governance", new[] { 3, 4 }) },
            Questions = new() { new("RQ1", "Do agents reduce defects?", answer1 ?? "Agents reduce defects [1].") },
            Included = new() { 1, 2, 3, 4 },
        };
    }

    public static PaperCheck Check(PaperCheckInput input, string id) => PaperSelfCheck.Run(input).Checks.Single(c => c.Id == id);
}

/// <summary>Checks 1 and 5 of the self-check: the numbers of the abstract and conclusion, and the result figures.</summary>
public class PaperSelfCheckNumbersTests
{
    [Fact]
    public void AnAbstractWhoseCountsMatchTheRunPasses()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(
            "A search identified 40 records; after removing 6 duplicates, 34 records were screened and 4 were included. Synthesis produced 2 themes. One study cut defects by 41%."), "numbers");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Empty(check.Evidence);
    }

    [Fact]
    public void ACountAttachedToTheWrongThingIsAFault()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input("The review included 34 studies."), "numbers");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        var e = Assert.Single(check.Evidence);
        Assert.Equal("abstractItem", e.Field);
        Assert.Contains("34 is given as studies included", e.Detail);
        Assert.Contains("34 is the number of records screened", e.Detail);
    }

    [Fact]
    public void ANumberFoundNowhereInTheRunIsAFault()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(conclusion: "Agents cut defects by 77% across settings."), "numbers");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Contains("77 is neither", Assert.Single(check.Evidence).Detail);
    }

    [Fact]
    public void CountsInWordsYearsAndNamesAreReadCorrectly()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(
            "Since 2020, two themes emerged from studies of GPT-4 and ISO 26262 compliance, and four studies were included."), "numbers");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
    }

    [Fact]
    public void AResultFigureInOneLaterPlaceAndTheAbstractPasses()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input("Defects fell by 41%.", answer1: "Agents cut defects by 41% [1]."), "figures");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
    }

    [Fact]
    public void AResultFigureRepeatedOrInTheConclusionIsAFault()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(answer1: "Agents cut defects by 41% [1].", discussion: "A 41% cut matters [1].",
            conclusion: "Defects fell by 41%, so agents help."), "figures");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        var e = Assert.Single(check.Evidence);
        Assert.Equal("researchAnswer1", e.Field);
        Assert.Equal("Answer to RQ1, Conclusion", e.Part);
        Assert.Contains("Agents cut defects by 41%", e.Text);
        Assert.Equal("41 from the results appears again in: Discussion, Answer to RQ1 and Conclusion. Conclusion should use none.", e.Detail);
    }

    [Fact]
    public void ThePartOfAPartitiveIsNotReadAsWhatTheNounCounts()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input("Full texts were read for 2 of the 4 full texts found."), "numbers");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
    }

    [Fact]
    public void TheSummaryLineCountsEachOutcome()
    {
        Assert.Equal("8 checks passed, 2 notes, 1 fault", PaperSelfCheck.SummaryLine(8, 2, 1));
        Assert.Equal("1 check passed", PaperSelfCheck.SummaryLine(1, 0, 0));
    }
}

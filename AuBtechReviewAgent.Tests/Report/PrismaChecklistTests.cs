using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The PRISMA 2020 checklist appendix (every item, where it is reported and an honest status) and the statement on what
/// the tool does not assess (items 12 to 14), both written in code.
/// </summary>
public class PrismaChecklistTests
{
    private static readonly string[] AllItems =
    {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "10a", "10b", "11", "12", "13a", "13b", "13c", "13d", "13e", "13f", "14",
        "15", "16a", "16b", "17", "18", "19", "20a", "20b", "20c", "20d", "21", "22", "23a", "23b", "23c", "23d", "24a", "24b", "24c",
        "25", "26", "27",
    };

    private static PrismaChecklistFacts Full() => new()
    {
        Title = "Agent loops: a systematic review", HasResearchQuestions = true, HasSynthesisMethods = true, HasCertainty = true,
        IncludedStudies = 4, HasEvidenceMap = true, ExtractionTable = "Table A.2", AppraisalTable = "Table A.3", SearchTable = "Table A.4",
        HasProtocol = true, FundingDeclared = true, HasEvidenceLimitations = true, HasReviewLimitations = true, HasImplications = true, HasFutureResearch = true,
    };

    private static ChecklistRow Row(IReadOnlyList<ChecklistRow> rows, string item) => rows.Single(r => r.Item == item);

    [Fact]
    public void EveryItemAndSubItemHasOneRowInPrismaOrder()
    {
        Assert.Equal(AllItems, PrismaChecklist.Rows(Full()).Select(r => r.Item));
        Assert.Equal(AllItems, PrismaChecklist.Rows(new PrismaChecklistFacts()).Select(r => r.Item));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("13e")]
    [InlineData("14")]
    [InlineData("20b")]
    [InlineData("20c")]
    [InlineData("21")]
    public void WhatTheToolDoesNotDoIsNeverClaimedAndSaysWhy(string item)
    {
        var row = Row(PrismaChecklist.Rows(Full()), item);

        Assert.Equal(ChecklistStatus.NotDone, row.Status);
        Assert.NotEqual("", row.Reason);
        Assert.Contains(MethodsSectionWriter.NotAssessedHeading, row.Where);
    }

    [Fact]
    public void AFullRunNamesItsSectionsAndTables()
    {
        var rows = PrismaChecklist.Rows(Full());

        Assert.Equal(ChecklistStatus.Reported, Row(rows, "1").Status);
        Assert.Contains("Research Questions", Row(rows, "4").Where);
        Assert.Equal("Search Execution (Methodology), Table A.4", Row(rows, "7").Where);
        Assert.Equal("Table A.3", Row(rows, "18").Where);
        Assert.Contains("Table A.2", Row(rows, "17").Where);
        Assert.Equal(ChecklistStatus.Reported, Row(rows, "15").Status);
        Assert.Equal(ChecklistStatus.InPart, Row(rows, "13f").Status);
        Assert.Equal(ChecklistStatus.InPart, Row(rows, "2").Status);
        Assert.Equal(ChecklistStatus.Authors, Row(rows, "24a").Status);
        Assert.Equal(ChecklistStatus.Authors, Row(rows, "26").Status);
        Assert.Equal(ChecklistStatus.Reported, Row(rows, "25").Status);
    }

    [Fact]
    public void AnEmptyRunMarksWhatIsMissingInsteadOfClaimingIt()
    {
        var rows = PrismaChecklist.Rows(new PrismaChecklistFacts { Title = "Agent loops" });

        Assert.Equal(ChecklistStatus.InPart, Row(rows, "1").Status);
        foreach (string item in new[] { "9", "10a", "10b", "11", "17", "18", "19" })
            Assert.Equal(ChecklistStatus.NotApplicable, Row(rows, item).Status);
        foreach (string item in new[] { "13a", "13f", "15", "20d", "22", "23b", "23d", "24b", "24c" })
            Assert.Equal(ChecklistStatus.NotDone, Row(rows, item).Status);
        Assert.Equal(ChecklistStatus.Authors, Row(rows, "25").Status);
        Assert.Equal(ChecklistStatus.InPart, Row(rows, "7").Status);
        Assert.All(rows.Where(r => r.Status is not ChecklistStatus.Reported), r => Assert.NotEqual("", r.Reason));
    }

    [Fact]
    public void TheTableIsEscapedLinksWhatItNamesAndCarriesAStatusPerRow()
    {
        var rows = PrismaChecklist.Rows(Full());
        string tex = PrismaChecklist.Latex(rows, "tab:A.5", PaperLatex.Escape);

        Assert.Contains(@"\caption{PRISMA 2020 checklist}\label{tab:A.5}", tex);
        Assert.Contains(@"25 & Support & Support \& Funding (Declarations) & Reported\\ \addlinespace", tex);
        Assert.Contains("12 & Effect measures & What This Review Did Not Assess (Methodology) & Not done by the tool: the findings were synthesised in words", tex);
        Assert.Contains("26 & Competing interests & Competing Interests (Declarations) & For the authors: only the authors can declare them", tex);
        Assert.Contains(@"Search Execution (Methodology), Table~\ref{tab:A.4}", tex);
        Assert.Contains(@"PRISMA 2020 flow diagram (Figure~\ref{fig:flow})", tex);
        Assert.Contains(@"Evidence Map of the Thematic Synthesis (Table~\ref{tab:evidence-map})", tex);
        Assert.Equal(rows.Count, tex.Split(@"\\ \addlinespace").Length - 1);
    }

    [Fact]
    public void TheNotAssessedStatementNamesEachItemTheToolDoesNotCarryOut()
    {
        string text = MethodsSectionWriter.NotAssessed(sensitivityCheck: false);

        Assert.Contains("did not compute effect measures (PRISMA 2020 item 12)", text);
        Assert.Contains("without a meta-analysis", text);
        Assert.Contains("(items 13d and 13e)", text);
        Assert.Contains("No sensitivity analysis was run (item 13f)", text);
        Assert.Contains("was not assessed (item 14)", text);
        Assert.DoesNotContain("Certainty of the Evidence", text);
    }

    [Fact]
    public void WithACertaintyRatingTheStatementNamesTheSensitivityCheckInsteadOfDenyingIt()
    {
        string text = MethodsSectionWriter.NotAssessed(sensitivityCheck: true);

        Assert.Contains("described under Certainty of the Evidence (item 13f)", text);
        Assert.DoesNotContain("No sensitivity analysis", text);
    }
}

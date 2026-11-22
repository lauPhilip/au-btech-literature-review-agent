using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A question about frequency answered from the map (#249): the paper and the data appendix give the same answer in
/// the same words. Before, the paper counted RQ1's practices for RQ2 while the appendix said "No theme was found".
/// </summary>
public class MultivocalFrequencyAnswerTests
{
    private static MultivocalReportInput CountedElsewhere()
    {
        var input = MultivocalPaperTests.Input();
        var map = input.Map.Versions[0];
        map.Attributes.RemoveAll(a => a.Name == "Practice mentioned");
        map.Attributes.Single(a => a.Name == "Practice").AlsoFor.Add("RQ2");
        return input;
    }

    private static string Section(string markdown, string heading)
    {
        int start = markdown.IndexOf("### " + heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "no section " + heading);
        int end = markdown.IndexOf("\n#", start + 4, StringComparison.Ordinal);
        return markdown[start..(end < 0 ? markdown.Length : end)];
    }

    [Fact]
    public void ThePaperAndTheDataAppendixGiveTheSameAnswerInTheSameWords()
    {
        var input = CountedElsewhere();
        var rq2 = input.Planned.Plan.Numbered().Single(q => q.Number == "RQ2");
        string paper = MultivocalPaperParts.CodeAnswer(input, "RQ2", rq2.Question).Text;
        string appendix = Section(MultivocalReporter.Write(input).ToMarkdown(), "RQ2:");

        string lead = MultivocalPaperParts.CodeAnswerLead(input, "RQ2", rq2.Question);
        Assert.StartsWith(lead, paper);
        Assert.Contains(lead, appendix);
        Assert.Contains("It counts the values of \"Practice\" (RQ1)", appendix);
        Assert.Contains("| Summarisation | 2 | 67% | [S1, S2] |", appendix); // the paper's [1, 2], as this report numbers sources
        Assert.Contains("Summarisation | 2 | 67% | [1, 2]", paper);
        Assert.Contains("A source can state several values, so the shares can add up to more than 100%.", appendix);
        Assert.DoesNotContain("No theme was found", appendix);
    }

    [Fact]
    public void AnExistenceQuestionIsAnsweredTheSameWayInTheAppendix()
    {
        var input = MultivocalPaperTests.Input();
        string appendix = Section(MultivocalReporter.Write(input).ToMarkdown(), "RQ1.1:");

        Assert.Contains("This question asks whether something exists", appendix);
        Assert.Contains("| LangGraph | 1 | 33% | [S1] |", appendix);
    }

    [Fact]
    public void AQuestionWithNoAttributeSaysSoInBoth()
    {
        var input = MultivocalPaperTests.Input();
        input.Map.Versions[0].Attributes.RemoveAll(a => a.Serves("RQ2"));
        var rq2 = input.Planned.Plan.Numbered().Single(q => q.Number == "RQ2");

        string expected = "The systematic map has no attribute for it, so it is not answered here.";
        Assert.Contains(expected, MultivocalPaperParts.CodeAnswer(input, "RQ2", rq2.Question).Text);
        Assert.Contains(expected, Section(MultivocalReporter.Write(input).ToMarkdown(), "RQ2:"));
    }
}

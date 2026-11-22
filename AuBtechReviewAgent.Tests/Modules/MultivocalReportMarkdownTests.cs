using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// How a multivocal report is written to multivocal-report.md (MultivocalReport.ToMarkdown): the title, the lines
/// under it, and each kind of block under a heading of its level.
/// </summary>
public class MultivocalReportMarkdownTests
{
    [Fact]
    public void EveryKindOfBlockIsWrittenAsMarkdown()
    {
        var report = new MultivocalReport { Title = "Context engineering: a grey literature review" };
        report.Meta.Add("Run 1");
        report.Meta.Add("Method: Garousi et al. (2019)");
        var method = new ReportSection { Heading = "1. Planning" };
        method.Blocks.Add(ReportBlock.Paragraph("The review was planned."));
        method.Blocks.Add(ReportBlock.Quote("A quote."));
        method.Blocks.Add(ReportBlock.List(new[] { "one", "two" }));
        report.Sections.Add(method);
        var question = new ReportSection { Heading = "RQ1: What?", Level = 3 };
        question.Blocks.Add(ReportBlock.Table(new[] { "Source", "Title" }, new[] { new[] { "S1", "A | B\nC" }, new[] { "S2", "D" } }));
        report.Sections.Add(question);

        string md = report.ToMarkdown().Replace("\r\n", "\n");

        Assert.Equal(
            "# Context engineering: a grey literature review\n\n" +
            "- Run 1\n- Method: Garousi et al. (2019)\n\n" +
            "## 1. Planning\n\n" +
            "The review was planned.\n\n" +
            "> A quote.\n\n" +
            "- one\n- two\n\n" +
            "### RQ1: What?\n\n" +
            "| Source | Title |\n|---|---|\n| S1 | A \\| B C |\n| S2 | D |\n\n",
            md);
    }

    [Fact]
    public void ABlockKeepsWhatItWasMadeFrom()
    {
        var table = ReportBlock.Table(new[] { "A" }, new[] { new[] { "1" } });

        Assert.Equal("paragraph", ReportBlock.Paragraph("x").Kind);
        Assert.Equal("quote", ReportBlock.Quote("x").Kind);
        Assert.Equal(new[] { "a", "b" }, ReportBlock.List(new[] { "a", "b" }).Items);
        Assert.Equal("table", table.Kind);
        Assert.Equal(new[] { "A" }, table.Headers);
        Assert.Equal("1", table.Rows[0][0]);
        Assert.Equal(2, new ReportSection().Level);
    }

    [Theory]
    [InlineData(ReviewAudience.Researchers, "researchers")]
    [InlineData(ReviewAudience.Practitioners, "practitioners")]
    [InlineData(ReviewAudience.Both, "researchers and practitioners")]
    [InlineData(null, "an audience that was not set")]
    public void TheAudienceIsWrittenInWords(ReviewAudience? audience, string words) =>
        Assert.Equal(words, MultivocalReporter.Audience(audience));
}

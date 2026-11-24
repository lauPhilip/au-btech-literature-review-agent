using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The grey literature review's side of the shared writing steps: which stages have a synthesis to write from
/// (MultivocalWriter.IsSynthesised) and how the writing prompts name the review and its parts (MultivocalWriter.Profile).
/// </summary>
public class MultivocalWriterProfileTests
{
    [Theory]
    [InlineData("Synthesised", true)]
    [InlineData("Writing", true)]
    [InlineData("Written", true)]
    [InlineData("Checking", true)]
    [InlineData("Checked", true)]
    [InlineData("Extracted", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyARunWithItsSynthesisCanBeWritten(string? stage, bool synthesised) =>
        Assert.Equal(synthesised, MultivocalWriter.IsSynthesised(stage));

    [Fact]
    public void TheProfileNamesTheReviewByItsKind()
    {
        var grey = MultivocalWriter.Profile(MultivocalPlan.Example("grey"));
        var multivocal = MultivocalPlan.Example("multivocal");
        multivocal.FormalDatabases = new List<string> { "arxiv" };

        Assert.Equal("a grey literature review following the Garousi et al. (2019) guidelines", grey.Review);
        Assert.Equal("a grey literature review", grey.ReviewShort);
        Assert.Equal("a multivocal literature review", MultivocalWriter.Profile(multivocal).ReviewShort);
        Assert.Equal("the Results section", grey.ResultsSection);
        Assert.Equal("the Discussion section", grey.DiscussionSection);
        Assert.Equal("source", grey.Source);
        Assert.Equal("sources", grey.Sources);
        Assert.Equal("counted in code from the run's files", grey.BaseFactsFrom);
        Assert.Equal("excerpts from the kept page texts of the included sources", grey.SourceTexts);
        Assert.StartsWith("(a) what the answers and the themes together say about the goal, (b) the state of the practice", grey.DiscussionParts);
        Assert.EndsWith("they are separate sections written after this one.", grey.DiscussionParts);
        Assert.DoesNotContain("academic", grey.DiscussionParts);
        Assert.Contains("the grey and the academic sources", MultivocalWriter.Profile(multivocal).DiscussionParts);
        // The questions keep the plan's numbers in every prompt, and the closing parts are named as Garousi et al. (2019) name them (issue #461).
        Assert.Equal(new[] { "RQ1", "RQ1.1", "RQ1.2", "RQ2", "RQ3" }, grey.QuestionIds);
        Assert.Contains("threats to validity", grey.LimitationsPart);
        Assert.Contains("practitioners", grey.ImplicationsPart);
        Assert.Contains("researchers", grey.FutureResearchPart);
    }

    [Fact]
    public void TheRunsFilesKeepTheirNames()
    {
        Assert.Equal("mlr-paper.json", MultivocalWriter.PaperFile);
        Assert.Equal("citation-audit.json", MultivocalWriter.AuditFile);
        Assert.Equal("grounded-outline.txt", MultivocalWriter.OutlineFile);
        Assert.Equal("peer-review-feedback.json", MultivocalWriter.PeerReviewFile);
        Assert.Equal(6, MultivocalWriter.WritingSteps);
    }
}

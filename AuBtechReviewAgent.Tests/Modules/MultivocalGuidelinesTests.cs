using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The fixed parts of the Garousi et al. (2019) guidelines the planning form asks about (MultivocalGuidelines): the
/// question types of Table 6, the kinds of grey literature, the searches, the stopping rules and their lookups.
/// </summary>
public class MultivocalGuidelinesTests
{
    [Fact]
    public void Table6HasItsTenTypesAndMarksThoseSourcesRarelyAnswer()
    {
        Assert.Equal(
            new[] { "existence", "description", "descriptive-comparative", "frequency", "process", "relationship", "causality", "causality-comparative", "causality-interaction", "design" },
            MultivocalGuidelines.QuestionTypes.Select(t => t.Key));
        Assert.Equal(new[] { "relationship", "causality", "causality-comparative", "causality-interaction", "design" },
            MultivocalGuidelines.QuestionTypes.Where(t => t.HardForSources).Select(t => t.Key));
        Assert.Equal("How often does X occur?", MultivocalGuidelines.FindType("frequency")!.Pattern);
        Assert.Null(MultivocalGuidelines.FindType("unknown"));
        Assert.Null(MultivocalGuidelines.FindType(null));
    }

    [Fact]
    public void TheKindsSearchesAndStoppingRulesKeepTheirKeysAndOrder()
    {
        Assert.Equal(new[] { "white-papers", "documentation", "blogs", "qa", "code", "news", "theses", "talks" }, MultivocalGuidelines.GreyTypes.Select(t => t.Key));
        Assert.Equal(new[] { "stackexchange", "github", "hackernews", "openalex-grey", "zenodo", "backlinks" }, MultivocalGuidelines.GreySearches.Select(s => s.Key));
        Assert.Equal(new[] { "effort", "saturation", "exhaustion" }, MultivocalGuidelines.StoppingRules.Select(r => r.Key));
        Assert.Equal(7, MultivocalGuidelines.IncludeGreyQuestions.Count);
        Assert.Equal(20, MultivocalGuidelines.QualityPointsMax);
    }

    [Fact]
    public void ASearchIsNamedByItsLabelAndAnUnknownOneByItsKey()
    {
        Assert.Equal("Hacker News", MultivocalGuidelines.SearchLabel("hackernews"));
        Assert.Equal("somewhere", MultivocalGuidelines.SearchLabel("somewhere"));
    }
}

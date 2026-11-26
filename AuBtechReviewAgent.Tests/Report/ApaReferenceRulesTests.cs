using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The APA 7 rules the audit of real runs c5ff8a41 and cce1c6e4 found broken: titles in capitals, preprints without
/// their number, proceedings without pages or publisher, volume, issue and pages from the enrichment, and titles that
/// end in a question mark.
/// </summary>
public class ApaReferenceRulesTests
{
    private static AcademicPaper Paper(string title, string venue, string? doi = null, string id = "OPENALEX_W1", List<string>? authors = null) =>
        new(id, title, "abstract", "Published: 2025", authors ?? new List<string> { "Abhishek Devgan" }, venue, doi);

    [Fact]
    public void ATitleInCapitalsBecomesSentenceCaseAndKeepsItsAcronyms()
    {
        var result = ApaCitationBuilder.Build(Paper("AGENTIC AI FRAMEWORKS FOR AUTONOMOUS AUTOMOTIVE SOFTWARE VALIDATION",
            "International Journal of Engineering Technology Research & Management", "10.5281/zenodo.20096249"));

        Assert.Contains("(2025). Agentic AI frameworks for autonomous automotive software validation. *International", result.Citation);
    }

    // Issue #567: run 289a0d3e mixed title case and sentence case in its reference list.
    [Theory]
    [InlineData("Agentic AI Frameworks for Autonomous Enterprise Software Development Workflows", "Agentic AI frameworks for autonomous enterprise software development workflows")]
    [InlineData("Towards Multi-Agentic AI for automated software design and modelling: challenges and opportunities", "Towards multi-agentic AI for automated software design and modelling: Challenges and opportunities")]
    [InlineData("LLMoxie: exploring agentic AI with Python", "LLMoxie: Exploring agentic AI with Python")]
    [InlineData("ReAct Loops in GitHub Agents for 3D Scenes", "ReAct loops in GitHub agents for 3D scenes")]
    [InlineData("Multi-Agent Systems: A Survey", "Multi-agent systems: A survey")]
    public void ATitleIsSetInSentenceCase(string title, string expected) =>
        Assert.Equal(expected, ApaCitationBuilder.TitleAsGiven(title + "."));

    [Fact]
    public void ATitleInSentenceCaseAlreadyIsLeftAsItIs() =>
        Assert.Equal("Agent loops for safe orchestration", ApaCitationBuilder.TitleAsGiven("Agent loops for safe orchestration"));

    [Fact]
    public void ACapitalTitleStartsAgainAfterAColon() =>
        Assert.Equal("LLM agents: A survey of memory", ApaCitationBuilder.TitleAsGiven("LLM AGENTS: A SURVEY OF MEMORY"));

    [Fact]
    public void ATitleEndingInAQuestionMarkGetsNoFullStop()
    {
        var result = ApaCitationBuilder.Build(Paper("Do we still need people?", "Future Internet"));

        Assert.Contains("Do we still need people? *Future Internet*.", result.Citation);
    }

    [Fact]
    public void AJournalArticleGetsVolumeIssueAndPagesFromKnownDetails()
    {
        var known = new ReferenceDetails { Volume = "6", Issue = "1", Pages = "122–130" };

        var result = ApaCitationBuilder.Build(Paper("Agentic AI frameworks", "International Journal of AI", "10.63282/x"), known);

        Assert.Equal("Devgan, A. (2025). Agentic AI frameworks. *International Journal of AI*, *6*(1), 122–130. https://doi.org/10.63282/x", result.Citation);
        Assert.Equal("6", result.Details!.Volume);
    }

    [Fact]
    public void AJournalThatNumbersArticlesGivesTheArticleNumber()
    {
        var result = ApaCitationBuilder.Build(Paper("Memory for agents", "IEEE Access", "10.1109/ACCESS.2025.1"),
            new ReferenceDetails { Volume = "13", ArticleNumber = "104512" });

        Assert.Contains("*IEEE Access*, *13*, Article 104512. https://doi.org/", result.Citation);
    }

    [Fact]
    public void AProceedingsPaperGetsItsPagesAndPublisher()
    {
        var result = ApaCitationBuilder.Build(Paper("Towards multi-agentic AI", "2025 40th IEEE/ACM International Conference on Automated Software Engineering Workshops (ASEW)",
            "10.1109/ASEW67777.2025.00063", authors: new List<string> { "Hoa Khanh Dam" }), new ReferenceDetails { Pages = "421–424", Publisher = "IEEE" });

        Assert.Equal("Dam, H. K. (2025). Towards multi-agentic AI. In *2025 40th IEEE/ACM International Conference on Automated Software Engineering Workshops (ASEW)* (pp. 421–424). IEEE. https://doi.org/10.1109/ASEW67777.2025.00063", result.Citation);
        Assert.Equal("Conferences", result.VenueType);
    }

    [Fact]
    public void AVenueThatRepeatsTheTitleIsABookThatStandsAlone()
    {
        string title = "Building AI Agents for Finance";
        var result = ApaCitationBuilder.Build(Paper(title, title));

        Assert.Equal("Devgan, A. (2025). *Building AI agents for finance*.", result.Citation);
    }

    [Fact]
    public void TheVenueStringStillGivesVolumeAndIssueAsDetails()
    {
        var result = ApaCitationBuilder.Build(Paper("Serious play", "Applied Sciences, Volume 14, Issue 2", id: "http://arxiv.org/abs/2407.00334v1"));

        Assert.Equal(("14", "2", "2407.00334"), (result.Details!.Volume, result.Details.Issue, result.Details.ArxivId));
    }

    [Theory]
    [InlineData("10.48550/arXiv.2607.02703", "2607.02703")]
    [InlineData("http://arxiv.org/abs/2607.02703v3", "2607.02703")]
    [InlineData("http://arxiv.org/abs/cs/0101001v1", "cs/0101001")]
    [InlineData("10.1145/3796507", null)]
    public void FindsTheArxivNumberWithoutItsVersion(string candidate, string? expected) =>
        Assert.Equal(expected, ReferenceDetails.ArxivIdOf(candidate));

    [Theory]
    [InlineData("Jun-wei Liu", "Liu, J.-W.")]
    [InlineData("Liu, Jun-wei", "Liu, J.-W.")]
    [InlineData("J.-P. Sartre", "Sartre, J.-P.")]
    public void AHyphenatedGivenNameKeepsItsHyphen(string raw, string expected) =>
        Assert.Equal(expected, ApaCitationBuilder.FormatAuthor(raw));
}

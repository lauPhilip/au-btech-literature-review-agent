using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The declarations a journal asks for (the publisher audit, batch A): placeholders the authors complete, a funding text
/// that never tells the reader how to set a configuration value, and the statement on AI use in each publisher's shape.
/// </summary>
public class PaperDeclarationsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Pending...")]
    [InlineData("No funding statement has been declared for this review. The funding and support text can be set in the Report:SupportStatement configuration value.")]
    public void AMissingOrOldFallbackFundingTextBecomesThePlaceholder(string? recorded)
    {
        Assert.Equal("To be completed by the authors.", PaperDeclarations.Funding(recorded));
    }

    [Fact]
    public void AFundingTextTheRunRecordedIsKept()
    {
        Assert.Equal("Funded by the Example Foundation.", PaperDeclarations.Funding(" Funded by the Example Foundation. "));
    }

    [Fact]
    public void ThePlaceholdersAreCompetingInterestsEthicsAndContributions()
    {
        var sections = PaperDeclarations.Placeholders();
        Assert.Equal(new[] { "Competing Interests", "Ethics Approval", "Author Contributions" }, sections.Select(s => s.Heading));
        Assert.StartsWith(PaperDeclarations.ToComplete, sections[0].Text);
        Assert.StartsWith("Not required; the review involved no human participants", sections[1].Text);
        Assert.Contains("CRediT", sections[2].Text);
        Assert.DoesNotContain("Aarhus", string.Join(" ", sections.Select(s => s.Text)) + PaperDeclarations.AuthorLine);
        Assert.Contains("ORCID", PaperDeclarations.AuthorLine);
    }

    [Fact]
    public void TheResponsibilitySentenceDoesNotClaimThatAnyoneCheckedTheOutput()
    {
        Assert.DoesNotContain("reviewed", PaperDeclarations.Responsibility);
        Assert.DoesNotContain("take full responsibility", PaperDeclarations.Responsibility);
    }

    [Fact]
    public void EachPublisherGetsItsOwnShapeEscapedForLatex()
    {
        string latex = PaperDeclarations.PublisherVariantsLatex("TraceableAI with model_x", "screen records", "Short & sweet.", "Long statement.");
        Assert.Contains(@"\paragraph{Declaration of generative AI and AI-assisted technologies in the manuscript preparation process}", latex);
        Assert.Contains("During the preparation of this work the authors used TraceableAI with model\\_x in order to screen records. " + PaperDeclarations.Responsibility, latex);
        Assert.Contains(@"\paragraph{Acknowledgments}", latex);
        Assert.Contains(@"Short \& sweet.", latex);
        Assert.Contains("Springer Nature and Wiley", latex);
        Assert.Contains("Long statement.", latex);
    }

    [Fact]
    public void TheArtifactSentenceSaysTheModelChoseTheContentAndCodeDrewIt()
    {
        Assert.Equal("", AiUseStatement.ArtifactSentence(null));
        Assert.Equal("", AiUseStatement.ArtifactSentence(new ReviewArtifact { Kind = ArtifactKinds.ConceptMap, Error = "no findings" }));
        string map = AiUseStatement.ArtifactSentence(new ReviewArtifact { Kind = ArtifactKinds.ConceptMap });
        Assert.Contains("concept map", map);
        Assert.Contains("chosen by the model from the findings and drawn in code", map);
        Assert.Contains("laid out in code", AiUseStatement.ArtifactSentence(new ReviewArtifact { Kind = ArtifactKinds.Table }));
    }

    [Fact]
    public void TheMethodsStatementNamesTheLanguageEditAndTheArtifact()
    {
        var state = new ReviewState { Stats = new ReviewStats() };
        string plain = AiUseStatement.Methods(state, Guid.Empty);
        Assert.Contains("language edit by the model, for clear and direct prose", plain);
        Assert.DoesNotContain("concept map", plain);
        string withMap = AiUseStatement.Methods(state, Guid.Empty, artifact: new ReviewArtifact { Kind = ArtifactKinds.ConceptMap });
        Assert.Contains("The content of the concept map", withMap);
        Assert.EndsWith(PaperDeclarations.Responsibility, withMap);
    }
}

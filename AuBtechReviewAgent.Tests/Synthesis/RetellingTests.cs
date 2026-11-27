using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A finding told again in other words (issue #744), with the sentences of run 866a897b: section 4.4 retold findings of
/// 4.1 and 4.3 without their figures, and a sentence of 4.1 summed up the three sentences before it.
/// </summary>
public class RetellingTests
{
    private static List<SynthesisSection> Run866(string performance) => new()
    {
        new() { Field = "synthesis-1", Heading = "Architectural safety mechanisms", Text =
            "Agent specialization improves performance in multi-agent pipelines [4]. " +
            "Generated tests need 40–90% less test authoring effort and give better fault coverage [2]." },
        new() { Field = "synthesis-3", Heading = "Governance and compliance", Text =
            "An enterprise simulation reports productivity gains, defect reduction, and security compliance gains [1]." },
        new() { Field = "synthesis-4", Heading = "Performance outcomes", Text = performance },
    };

    [Fact]
    public void AFindingToldAgainInOtherWordsIsARepeatOfTheThemeThatToldItFirst()
    {
        var repeats = ReviewWriter.RepeatedClaims(Run866(
            "The simulation shows measurable gains in security compliance, defect reduction, productivity [1]. " +
            "Test generation reduced test authoring effort and improved fault coverage [2]."));

        Assert.Equal(2, repeats.Count);
        Assert.Equal(("synthesis-4", "The simulation shows measurable gains in security compliance, defect reduction, productivity [1].", "Governance and compliance"), repeats[0]);
        Assert.Equal("Architectural safety mechanisms", repeats[1].EarlierHeading);
    }

    [Fact]
    public void ASentenceIsJudgedOneCitedSourceAtATime()
    {
        var repeats = ReviewWriter.RepeatedClaims(Run866(
            "Benchmarks of latency under load vary widely across vendors [7], while agent specialization improves performance [4]."));

        Assert.Equal("Architectural safety mechanisms", Assert.Single(repeats).EarlierHeading);
    }

    [Fact]
    public void ANewFindingOfTheSameSourceIsNotARepeat()
    {
        Assert.Empty(ReviewWriter.RepeatedClaims(Run866(
            "The same simulation measured audit latency of agent actions in a staging cluster [1].")));
    }

    [Fact]
    public void ASentenceThatSumsUpItsOwnSubsectionIsARepeat()
    {
        var sections = new List<SynthesisSection>
        {
            new() { Field = "synthesis-1", Heading = "Layered architectures", Text =
                "A layered architecture separates planning from execution guards [1]. " +
                "Distributed verification checks each agent's output before it is used [2]. " +
                "Based on abstract-only evidence, [1] suggests a layered architecture separating planning from execution, while [2] emphasizes distributed verification of agent output." },
        };

        var repeat = Assert.Single(ReviewWriter.RepeatedClaims(sections));
        Assert.Equal("Layered architectures", repeat.EarlierHeading);
        Assert.StartsWith("Based on abstract-only evidence", repeat.Sentence);
    }

    [Fact]
    public void ASentenceThatComparesTwoSourcesFirstIsNotASumming()
    {
        var sections = new List<SynthesisSection>
        {
            new() { Field = "synthesis-1", Heading = "Layered architectures", Text =
                "A layered architecture separates planning from execution guards [1]. " +
                "Both designs fail open when the guard model times out under load [1, 2]." },
        };

        Assert.Empty(ReviewWriter.RepeatedClaims(sections));
    }

    [Fact]
    public void TheWordsOfACitationAreThoseBeforeItsMarkerOrAfterAMarkerThatOpensTheClause()
    {
        var parts = ReviewWriter.CitedParts("[3] identifies runtime policy checks, while guard models block unsafe calls [5].");

        Assert.Equal(new[] { 3, 5 }, parts.Select(p => p.Reference));
        Assert.Contains("runtime", parts[0].Words);
        Assert.Contains("guard", parts[1].Words);
        Assert.DoesNotContain("while", parts[1].Words);
    }
}

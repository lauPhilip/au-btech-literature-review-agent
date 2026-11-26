using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Issue #566: arXiv's journal reference went into the reference whole, as one italic venue.</summary>
public class JournalRefTests
{
    [Fact]
    public void TheJournalReferenceOfRun289IsSplitIntoItsParts()
    {
        var parts = JournalRef.Parse("Auton. Agents Multi Agent Syst. 38(1): 4 (2024)");
        Assert.Equal(new JournalRefParts("Auton. Agents Multi Agent Syst.", "38", "1", null, "4", 2024), parts);
    }

    [Fact]
    public void APageRangeAndATrailingYearAreRead()
    {
        Assert.Equal(new JournalRefParts("IEEE Access", "12", null, "100–110", null, 2023), JournalRef.Parse("IEEE Access 12, 100-110, 2023"));
        Assert.Equal(new JournalRefParts("Proceedings of AAMAS 2023", null, null, null, null, 2023), JournalRef.Parse("Proceedings of AAMAS 2023 (2023)"));
    }

    [Theory]
    [InlineData("Future Internet")]
    [InlineData("Proceedings of AAMAS 2023")]
    [InlineData("")]
    public void AVenueWithoutThoseShapesIsLeftAlone(string venue) => Assert.Null(JournalRef.Parse(venue));

    [Fact]
    public void ThePreprintIsCitedWithTheJournalsYearVolumeAndArticleNumber()
    {
        var paper = new AcademicPaper("http://arxiv.org/abs/2203.08975v2", "A survey of multi-agent deep reinforcement learning with communication", "",
            "Published: 2022", new() { "Changxi Zhu", "Mehdi Dastani", "Shihan Wang" }, "Auton. Agents Multi Agent Syst. 38(1): 4 (2024)",
            "10.48550/arXiv.2203.08975", "https://arxiv.org/abs/2203.08975v2");
        var apa = ApaCitationBuilder.Build(paper);

        Assert.Equal("Zhu, C., Dastani, M., & Wang, S. (2024). A survey of multi-agent deep reinforcement learning with communication. *Auton. Agents Multi Agent Syst.*, *38*(1), Article 4. Preprint: https://doi.org/10.48550/arXiv.2203.08975", apa.Citation);
        Assert.Equal(("Journals", 2024), (apa.VenueType, apa.Year));
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The version a run was made with: read from the git tag, shown on pages and cited in the paper.</summary>
public class ToolVersionTests
{
    private const string Sha = "40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961";

    [Fact]
    public void ABuildOnATagIsThatRelease()
    {
        string v = $"0.2.0+{Sha}";
        Assert.Equal("0.2.0", ToolVersion.Number(v));
        Assert.Equal(Sha, ToolVersion.Commit(v));
        Assert.True(ToolVersion.IsRelease(v));
        Assert.Equal("v0.2.0 (40b00a0)", ToolVersion.Display(v));
        Assert.Equal("https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.2.0", ToolVersion.ReleaseUrl(v));
        Assert.Equal("This run used version 0.2.0 (commit 40b00a0), released at https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.2.0.",
            ToolVersion.PaperSentence(v));
    }

    [Fact]
    public void ABuildBetweenTagsIsAPreReleaseAndLinksToItsCommit()
    {
        string v = $"0.2.1-alpha.0.3+{Sha}";
        Assert.Equal("0.2.1-alpha.0.3", ToolVersion.Number(v));
        Assert.False(ToolVersion.IsRelease(v));
        Assert.Null(ToolVersion.ReleaseUrl(v));
        Assert.Equal("v0.2.1-alpha.0.3 (40b00a0)", ToolVersion.Display(v));
        Assert.Equal($"https://github.com/lauPhilip/au-btech-literature-review-agent/tree/{Sha}", ToolVersion.CodeUrl(v));
        Assert.Contains("which is not a published release", ToolVersion.PaperSentence(v));
    }

    [Fact]
    public void TheCommitIsTheLastPartOfTheBuildMetadata()
    {
        Assert.Equal(Sha, ToolVersion.Commit($"0.2.0+build.7.{Sha}"));
        Assert.Null(ToolVersion.Commit("0.2.0"));
        Assert.Null(ToolVersion.Commit("0.2.0+local"));
    }

    [Fact]
    public void AVersionWithoutACommitIsShownAsItIs()
    {
        Assert.Equal("v0.0.0-local", ToolVersion.Display("0.0.0-local"));
        Assert.Equal("unknown", ToolVersion.Display(""));
        Assert.Equal("This run used version 0.3.0, released at https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.3.0.",
            ToolVersion.PaperSentence("0.3.0"));
        Assert.Equal("https://github.com/lauPhilip/au-btech-literature-review-agent", ToolVersion.CodeUrl("0.0.0-local"));
    }
}

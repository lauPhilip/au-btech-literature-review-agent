using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class CitationValidatorTests
{
    [Fact]
    public void KeepsValidMarker()
    {
        var result = CitationValidator.ValidateAndStrip("Fault isolation improves [3].", 5, "synthesisResultsItem");

        Assert.Equal("Fault isolation improves [3].", result.CleanedText);
        Assert.Empty(result.Stripped);
    }

    [Fact]
    public void RemovesOutOfRangeMarker()
    {
        var result = CitationValidator.ValidateAndStrip("As shown [56].", 53, "discussionItem");

        Assert.Equal("As shown .", result.CleanedText);
        var stripped = Assert.Single(result.Stripped);
        Assert.Equal("56", stripped.InvalidMarker);
        Assert.Equal("discussionItem", stripped.Field);
        Assert.Equal(53, stripped.ReferenceListSize);
    }

    [Fact]
    public void KeepsValidAndDropsInvalidInsideGroupedMarker()
    {
        var result = CitationValidator.ValidateAndStrip("Both [2, 75] agree.", 53, "field");

        Assert.Equal("Both [2] agree.", result.CleanedText);
        var stripped = Assert.Single(result.Stripped);
        Assert.Equal("75", stripped.InvalidMarker);
    }

    [Fact]
    public void RemovesGroupedMarkerEntirelyWhenAllInvalid()
    {
        var result = CitationValidator.ValidateAndStrip("Nope [99, 100].", 53, "field");

        Assert.Equal("Nope .", result.CleanedText);
        Assert.Equal(2, result.Stripped.Count);
    }

    [Fact]
    public void TreatsZeroAsInvalid()
    {
        var result = CitationValidator.ValidateAndStrip("Zero [0].", 5, "field");

        Assert.Equal("Zero .", result.CleanedText);
        Assert.Single(result.Stripped);
    }

    [Fact]
    public void KeepsBoundaryNumberEqualToReferenceCount()
    {
        var result = CitationValidator.ValidateAndStrip("Last [53].", 53, "field");

        Assert.Equal("Last [53].", result.CleanedText);
        Assert.Empty(result.Stripped);
    }

    [Fact]
    public void HandlesSpacesInsideGroupedMarker()
    {
        var result = CitationValidator.ValidateAndStrip("See [2 , 75].", 53, "field");

        Assert.Equal("See [2].", result.CleanedText);
        Assert.Single(result.Stripped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsEmptyOrBlankTextUnchangedWithNoStrips(string input)
    {
        var result = CitationValidator.ValidateAndStrip(input, 10, "field");

        Assert.Equal(input, result.CleanedText);
        Assert.Empty(result.Stripped);
    }

    [Fact]
    public void ReturnsTextUnchangedWhenReferenceCountIsZero()
    {
        var result = CitationValidator.ValidateAndStrip("Has [3] marker.", 0, "field");

        Assert.Equal("Has [3] marker.", result.CleanedText);
        Assert.Empty(result.Stripped);
    }

    [Fact]
    public void HandlesMultipleSeparateMarkersInOneField()
    {
        var result = CitationValidator.ValidateAndStrip("A [1] and B [99] and C [2].", 3, "field");

        Assert.Equal("A [1] and B  and C [2].", result.CleanedText);
        var stripped = Assert.Single(result.Stripped);
        Assert.Equal("99", stripped.InvalidMarker);
    }
}

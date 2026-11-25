using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class CitationMarkersTests
{
    [Theory]
    [InlineData("Loops recover [1, 2, 3, 4].", "Loops recover [1–4].")]
    [InlineData("Audits help [4, 1, 3].", "Audits help [1, 3, 4].")]
    [InlineData("Twice [2, 2].", "Twice [2].")]
    [InlineData("Mixed [7, 3, 5, 4, 9].", "Mixed [3–5, 7, 9].")]
    [InlineData("Expanded [2-4, 6].", "Expanded [2–4, 6].")]
    [InlineData("Two [1, 2] and one [3].", "Two [1, 2] and one [3].")]
    public void SortsDedupesAndRangesTheNumbersOfAMarker(string text, string expected) =>
        Assert.Equal(expected, CitationMarkers.Tidy(text));

    [Theory]
    [InlineData("The scale runs over [0, 1].")]
    [InlineData("A table [H] and a note [see 3].")]
    [InlineData("A falling range [5-2].")]
    public void LeavesAnythingThatIsNotAListOfReferenceNumbers(string text) =>
        Assert.Equal(text, CitationMarkers.Tidy(text));
}

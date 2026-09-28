using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The MMAT table lists only appraised studies; the non-empirical ones are named in a note under it.</summary>
public class MmatTableTests
{
    private static StudyExtraction Study(int n, string category, string? error = null) =>
        new() { ReferenceNumber = n, AppraisalCategory = category, Error = error };

    [Fact]
    public void NonEmpiricalStudiesAreLeftOutButFailedExtractionsStay()
    {
        Assert.False(MethodsSectionWriter.ShowInAppraisalTable(Study(1, "not_empirical")));
        Assert.True(MethodsSectionWriter.ShowInAppraisalTable(Study(2, "quantitative_nonrandomized")));
        Assert.True(MethodsSectionWriter.ShowInAppraisalTable(Study(3, "", error: "timeout")));
    }

    [Fact]
    public void TheNoteNamesTheLeftOutStudiesWithRanges()
    {
        var studies = new[]
        {
            Study(1, "not_empirical"), Study(2, "qualitative"), Study(4, "not_empirical"), Study(5, "not_empirical"),
            Study(10, "not_empirical"), Study(11, "not_empirical"), Study(13, "not_empirical"), Study(14, "not_empirical"),
            Study(15, "not_empirical"), Study(16, "not_empirical"),
        };
        string note = MethodsSectionWriter.NotAppraisedNote(studies, "-");
        Assert.Equal("Not shown: 9 studies ([1], [4], [5], [10], [11] and [13]-[16]) were classified as non-empirical (for example reviews or position papers), to which MMAT does not apply.", note);
    }

    [Fact]
    public void NoNoteWhenEveryStudyIsAppraised()
    {
        Assert.Equal("", MethodsSectionWriter.NotAppraisedNote(new[] { Study(1, "qualitative") }));
        Assert.StartsWith("Not shown: 1 study ([7]) was", MethodsSectionWriter.NotAppraisedNote(new[] { Study(7, "not_empirical") }));
    }
}

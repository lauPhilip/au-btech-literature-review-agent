using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The gallery's year and version filters and the description a shared link shows (issue #542).</summary>
public class GalleryFilterTests
{
    private static GalleryEntry Entry(string title, int year, string version, params string[] questions) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Card = "systematic",
        Title = title,
        CompletedUtc = new DateTime(year, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        ToolVersion = version,
        Questions = questions.ToList(),
    };

    [Theory]
    [InlineData("0.7.0+40b00a0c1234567", "0.7")]
    [InlineData("0.7.1-alpha.0.3+40b00a0", "0.7")]
    [InlineData("0.10.2", "0.10")]
    [InlineData("1.4.0+abcdef1234567", "1.4")]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    public void TheReleaseLineIsTheMajorAndMinorVersion(string version, string? line) =>
        Assert.Equal(line, GallerySearch.LineOf(Entry("x", 2026, version)));

    [Theory]
    [InlineData("2026", 2026)]
    [InlineData(" 2026", null)]
    [InlineData("-2026", null)]
    [InlineData("1999", null)]
    [InlineData("20260", null)]
    [InlineData("<script>", null)]
    [InlineData(null, null)]
    public void OnlyAPlausibleYearIsRead(string? text, int? year) => Assert.Equal(year, GallerySearch.CleanYear(text));

    [Theory]
    [InlineData("0.7", "0.7")]
    [InlineData("10.12", "10.12")]
    [InlineData("0.7.1", null)]
    [InlineData("v0.7", null)]
    [InlineData("0.7\"><script>", null)]
    [InlineData("", null)]
    public void OnlyAReleaseLineIsRead(string? text, string? line) => Assert.Equal(line, GallerySearch.CleanLine(text));

    [Fact]
    public void TheFiltersNarrowByYearAndLineAndOfferNewestFirst()
    {
        var a = Entry("A", 2025, "0.6.0");
        var b = Entry("B", 2026, "0.7.0");
        var c = Entry("C", 2026, "0.10.1");
        var d = Entry("D", 2026, "");
        var all = new[] { a, b, c, d };

        Assert.Equal(new[] { b, c, d }, GallerySearch.Narrow(all, 2026, null));
        Assert.Equal(new[] { c }, GallerySearch.Narrow(all, 2026, "0.10"));
        Assert.Empty(GallerySearch.Narrow(all, 2025, "0.7"));
        Assert.Equal(all, GallerySearch.Narrow(all, null, null));
        Assert.Equal(new[] { 2026, 2025 }, GallerySearch.Years(all));
        Assert.Equal(new[] { "0.10", "0.7", "0.6" }, GallerySearch.Lines(all));
    }

    [Theory]
    [InlineData(1, "tutors", null, null, "1 report matches \"tutors\".")]
    [InlineData(2, "tutors", 2026, "0.7", "2 reports match \"tutors\", finished in 2026, made with TraceableAI 0.7.")]
    [InlineData(0, "tutors", null, null, "No report matches \"tutors\". Try fewer or other words.")]
    [InlineData(3, null, 2026, null, "3 reports match: finished in 2026.")]
    [InlineData(0, null, 2025, "0.7", "No report matches: finished in 2025, made with TraceableAI 0.7. Choose another year or version.")]
    public void TheResultLineSaysWhatWasAskedInWords(int count, string? query, int? year, string? line, string expected) =>
        Assert.Equal(expected, GallerySearch.ResultLine(count, query, year, line));

    [Fact]
    public void ASharedLinkDescribesTheKindOfReportAndItsFirstQuestion()
    {
        string text = GalleryCard.ShareDescription(Entry("x", 2026, "0.7.0", "  How do  AI tutors\nchange feedback?", "Second?"));
        Assert.StartsWith("An example systematic literature review made with TraceableAI", text, StringComparison.Ordinal);
        Assert.EndsWith("How do AI tutors change feedback?", text, StringComparison.Ordinal);

        string none = GalleryCard.ShareDescription(Entry("x", 2026, "0.7.0"));
        Assert.EndsWith("against its source.", none, StringComparison.Ordinal);

        string longOne = GalleryCard.ShareDescription(Entry("x", 2026, "0.7.0", string.Join(' ', Enumerable.Repeat("question", 40))));
        Assert.True(longOne.Length <= 200, longOne);
        Assert.EndsWith("question…", longOne, StringComparison.Ordinal);
    }
}

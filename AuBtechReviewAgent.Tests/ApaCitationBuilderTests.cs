using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class ApaCitationBuilderTests
{
    private static AcademicPaper Paper(
        string id = "SCOPUS_ID:1",
        string title = "GridSage: A skill-grounded LLM platform",
        string date = "Published: 2025",
        List<string>? authors = null,
        string venue = "Electric Power Systems Research",
        string? doi = null,
        string? url = null) =>
        new(id, title, "abstract", date, authors ?? new List<string> { "Yu Ge" }, venue, doi, url);

    [Fact]
    public void BuildsJournalReferenceFromMetadata()
    {
        var result = ApaCitationBuilder.Build(Paper(doi: "10.1016/j.epsr.2025.109876"));

        Assert.Equal("Ge, Y. (2025). GridSage: A skill-grounded LLM platform. *Electric Power Systems Research*. https://doi.org/10.1016/j.epsr.2025.109876", result.Citation);
        Assert.Equal("Journals", result.VenueType);
        Assert.Equal("Electric Power Systems Research", result.VenueName);
        Assert.Equal(2025, result.Year);
    }

    [Fact]
    public void UnknownYearIsNdNotTheCurrentYear()
    {
        var result = ApaCitationBuilder.Build(Paper(date: "Published: "));

        Assert.Contains("(n.d.)", result.Citation);
        Assert.Equal(0, result.Year);
    }

    [Theory]
    [InlineData("https://doi.org/XXX")]
    [InlineData("XXXXX")]
    [InlineData("2607.02703v1")]
    public void NeverPrintsPlaceholderOrInvalidDoi(string doi)
    {
        var result = ApaCitationBuilder.Build(Paper(doi: doi));

        Assert.DoesNotContain("doi.org", result.Citation);
    }

    [Fact]
    public void ArxivPreprintWithoutJournalRefIsAPreprint()
    {
        var paper = Paper(
            id: "http://arxiv.org/abs/2607.02703v1",
            title: "LLMoxie: Exploring Agentic AI for Scientific Software Development",
            date: "Published: 2026",
            authors: new List<string> { "Lucas Setiawan", "Anshul Mittal" },
            venue: "arXiv Preprint Repository (arXiv:2607.02703v1)",
            doi: "10.48550/arXiv.2607.02703");

        var result = ApaCitationBuilder.Build(paper);

        Assert.Equal("Setiawan, L., & Mittal, A. (2026). LLMoxie: Exploring Agentic AI for Scientific Software Development. arXiv. https://doi.org/10.48550/arXiv.2607.02703", result.Citation);
        Assert.Equal("Preprints", result.VenueType);
        Assert.Equal("", result.VenueName);
    }

    [Fact]
    public void ConferenceVenueUsesInPrefix()
    {
        var result = ApaCitationBuilder.Build(Paper(venue: "2025 40th IEEE/ACM International Conference on Automated Software Engineering Workshops (ASEW)"));

        Assert.Contains("In *2025 40th IEEE/ACM International Conference", result.Citation);
        Assert.Equal("Conferences", result.VenueType);
    }

    [Fact]
    public void NoAuthorsMovesTitleToTheFront()
    {
        var result = ApaCitationBuilder.Build(Paper(authors: new List<string>()));

        Assert.StartsWith("GridSage: A skill-grounded LLM platform. (2025).", result.Citation);
    }

    [Theory]
    [InlineData("Scopus Indexed Journal")]
    [InlineData("Google Scholar Indexed Publication")]
    [InlineData("IEEE Publication")]
    public void PlaceholderVenuesAreDropped(string venue)
    {
        var result = ApaCitationBuilder.Build(Paper(venue: venue));

        Assert.DoesNotContain(venue, result.Citation);
        Assert.Equal("", result.VenueName);
    }

    [Theory]
    [InlineData("Jane A. Doe", "Doe, J. A.")]
    [InlineData("A Bandi", "Bandi, A.")]
    [InlineData("Doe, J.", "Doe, J.")]
    [InlineData("Jean-Paul Sartre", "Sartre, J. P.")]
    [InlineData("…", "")]
    public void FormatsSingleAuthor(string raw, string expected)
    {
        Assert.Equal(expected, ApaCitationBuilder.FormatAuthor(raw));
    }

    [Fact]
    public void FormatsThreeAuthorsWithAmpersand()
    {
        Assert.Equal("Ab, A., Cd, B., & Ef, C.", ApaCitationBuilder.FormatAuthorList(new[] { "A Ab", "B Cd", "C Ef" }));
    }

    [Fact]
    public void TruncatesMoreThanTwentyAuthors()
    {
        var authors = Enumerable.Range(1, 25).Select(i => $"A Author{i}").ToList();

        string list = ApaCitationBuilder.FormatAuthorList(authors);

        Assert.Contains("Author19, A., . . . Author25, A.", list);
        Assert.DoesNotContain("Author20", list);
    }

    [Fact]
    public void ReferenceOrderIsCaseInsensitiveAndStable()
    {
        var citations = new[] { "setiawan, L. (2026)", "Bandi, A. (2025)", "Pajo, P. (2025)" };

        var ordered = citations.OrderBy(c => c, ApaCitationBuilder.ReferenceOrder).ToList();

        Assert.Equal(new[] { "Bandi, A. (2025)", "Pajo, P. (2025)", "setiawan, L. (2026)" }, ordered);
    }
}

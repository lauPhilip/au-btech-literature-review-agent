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
            title: "LLMoxie: Exploring agentic AI for scientific software development",
            date: "Published: 2026",
            authors: new List<string> { "Lucas Setiawan", "Anshul Mittal" },
            venue: "arXiv Preprint Repository (arXiv:2607.02703v1)",
            doi: "10.48550/arXiv.2607.02703");

        var result = ApaCitationBuilder.Build(paper);

        // APA 7 (10.8): a preprint stands alone, so its title is in italics, followed by its arXiv number.
        Assert.Equal("Setiawan, L., & Mittal, A. (2026). *LLMoxie: Exploring agentic AI for scientific software development* (arXiv:2607.02703). arXiv. https://doi.org/10.48550/arXiv.2607.02703", result.Citation);
        Assert.Equal("Preprints", result.VenueType);
        Assert.Equal("", result.VenueName);
    }

    [Fact]
    public void AJournalReferenceWithOnlyTheArxivDoiSaysTheLinkIsThePreprint()
    {
        // Issue #151: "Applied Sciences, Volume 14, Issue 2. https://doi.org/10.48550/arXiv.2407.00334" put the
        // volume inside the italic venue and gave the preprint's DOI as if it were the journal's.
        var paper = Paper(id: "http://arxiv.org/abs/2407.00334v1", authors: new List<string> { "Ana Lopes" },
            title: "Serious play in engineering education", date: "Published: 2024",
            venue: "Applied Sciences, Volume 14, Issue 2", doi: "10.48550/arXiv.2407.00334");

        var result = ApaCitationBuilder.Build(paper);

        Assert.Equal("Lopes, A. (2024). Serious play in engineering education. *Applied Sciences*, *14*(2). Preprint: https://doi.org/10.48550/arXiv.2407.00334", result.Citation);
        Assert.Equal("Journals", result.VenueType);
        Assert.Equal("Applied Sciences", result.VenueName);
    }

    [Theory]
    [InlineData("Applied Sciences, Volume 14, Issue 2", "*Applied Sciences*, *14*(2).", "Applied Sciences")]
    [InlineData("IEEE Access, vol. 12, no. 3, pp. 10-20", "*IEEE Access*, *12*(3), 10\u201320.", "IEEE Access")]
    [InlineData("Computers & Education, Vol. 7", "*Computers & Education*, *7*.", "Computers & Education")]
    [InlineData("IEEE Transactions on Education, Volume 66, Issue 1", "*IEEE Transactions on Education*, *66*(1).", "IEEE Transactions on Education")]
    [InlineData("Journal of Volume Studies", "*Journal of Volume Studies*.", "Journal of Volume Studies")]
    public void VolumeIssueAndPagesFollowTheItalicVenueAsInApa(string venue, string expectedPart, string expectedName)
    {
        var result = ApaCitationBuilder.Build(Paper(venue: venue, doi: "10.1016/j.epsr.2025.109876"));

        Assert.Contains(" " + expectedPart + " https://doi.org/10.1016/j.epsr.2025.109876", result.Citation);
        Assert.Equal(expectedName, result.VenueName);
        Assert.DoesNotContain("Preprint", result.Citation);
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
    [InlineData("Jean-Paul Sartre", "Sartre, J.-P.")]
    [InlineData("…", "")]
    [InlineData("Liu J.", "Liu, J.")]
    [InlineData("Wang J.P.", "Wang, J. P.")]
    [InlineData("Gangadharan G R", "Gangadharan, G. R.")]
    [InlineData("Arunkumar .V", "Arunkumar, V.")]
    [InlineData("Van der Berg A.", "Van der Berg, A.")]
    [InlineData("Jian LI", "LI, J.")]
    [InlineData(" PSBC LLM Team", "PSBC LLM Team")]
    [InlineData("et al.", "et al.")]
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

    [Fact]
    public void ReferenceOrderSortsAccentedNamesWithTheirLetterAsApaDoes()
    {
        // Ordinal order put every accented initial after "Z": "Öner" and "Østergaard" came last.
        var citations = new[]
        {
            "Zhang, W. (2025)", "Öner, B. (2024)", "Oliveira, C. (2023)", "Østergaard, D. (2025)", "Pajo, P. (2025)",
            "Ölçer, E. (2022)", "Leeds, A. (2021)", "Lee, J. (2020)", "*Agentic AI handbook* (2024)", "Éclair, F. (2020)",
        };

        var ordered = citations.OrderBy(c => c, ApaCitationBuilder.ReferenceOrder).ToList();

        Assert.Equal(new[]
        {
            "*Agentic AI handbook* (2024)", "Éclair, F. (2020)", "Lee, J. (2020)", "Leeds, A. (2021)", "Ölçer, E. (2022)",
            "Oliveira, C. (2023)", "Öner, B. (2024)", "Østergaard, D. (2025)", "Pajo, P. (2025)", "Zhang, W. (2025)",
        }, ordered);
        Assert.Equal(0, ApaCitationBuilder.ReferenceOrder.Compare("Öner, B.", "oner, b."));
    }

    [Fact]
    public void ItalicSpansSetTheJournalAndVolumeInItalicsWithoutAsterisks()
    {
        var spans = ApaCitationBuilder.ItalicSpans("Ge, Y. (2025). GridSage. *Applied Sciences*, *14*(2), 1\u201310.");

        Assert.Equal(new[]
        {
            new ApaCitationBuilder.TextSpan("Ge, Y. (2025). GridSage. ", false),
            new ApaCitationBuilder.TextSpan("Applied Sciences", true),
            new ApaCitationBuilder.TextSpan(", ", false),
            new ApaCitationBuilder.TextSpan("14", true),
            new ApaCitationBuilder.TextSpan("(2), 1\u201310.", false),
        }, spans);
        Assert.DoesNotContain(spans, s => s.Text.Contains('*'));
    }

    [Fact]
    public void ItalicSpansOfABuiltReferenceJoinBackToItsTextWithoutAsterisks()
    {
        var paper = Paper(venue: "Applied Sciences, Volume 14, Issue 2", doi: "10.3390/app14020001");
        string citation = ApaCitationBuilder.Build(paper).Citation;

        var spans = ApaCitationBuilder.ItalicSpans(citation);

        Assert.Equal(citation.Replace("*", ""), string.Concat(spans.Select(s => s.Text)));
        Assert.Contains(new ApaCitationBuilder.TextSpan("Applied Sciences", true), spans);
        Assert.Contains(new ApaCitationBuilder.TextSpan("14", true), spans);
    }

    [Theory]
    [InlineData("Plain reference without a venue.", "Plain reference without a venue.")]
    [InlineData("An older run with one stray * asterisk.", "An older run with one stray  asterisk.")]
    public void ItalicSpansShowTextWithoutPairedAsterisksPlain(string citation, string shown)
    {
        var spans = ApaCitationBuilder.ItalicSpans(citation);

        Assert.Equal(new[] { new ApaCitationBuilder.TextSpan(shown, false) }, spans);
    }

    [Fact]
    public void ItalicSpansOfNothingAreEmpty()
    {
        Assert.Empty(ApaCitationBuilder.ItalicSpans(null));
        Assert.Empty(ApaCitationBuilder.ItalicSpans(""));
    }

    [Fact]
    public void AListGivenOnlyInPartEndsWithEtAl()
    {
        var authors = new List<string> { "Liu J.", ApaCitationBuilder.EtAl };

        Assert.Equal("Liu, J., et al.", ApaCitationBuilder.FormatAuthorList(authors));
        Assert.StartsWith("Liu, J., et al. (2026).", ApaCitationBuilder.Build(Paper(authors: authors, date: "Published: 2026")).Citation);
    }

    [Fact]
    public void ExportsWriteASurnameFirstNameTheRightWayRoundAndEtAlAsOthers()
    {
        var row = new IncludedPaperMetricRow { Title = "SMARF: semantic memory", Year = 2026, Authors = new() { "Liu J.", ApaCitationBuilder.EtAl }, ReferenceNumber = 1 };

        string bib = BibliographyExporter.ToBibTeX(new[] { row });
        string ris = BibliographyExporter.ToRis(new[] { row });

        Assert.Contains("author = {Liu, J. and others}", bib);
        Assert.Contains("@article{liu2026smarf", bib.Replace("@misc", "@article"));
        Assert.Contains("AU  - Liu, J.", ris);
        Assert.DoesNotContain("et al", ris);
    }
}

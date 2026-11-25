using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>BibTeX and RIS carry volume, issue, pages, publisher and the arXiv number, as the reference list does.</summary>
public class BibliographyDetailsTests
{
    private static IncludedPaperMetricRow Row(string venueType, string venue, string? doi, ReferenceDetails? details, string author = "Hoa Khanh Dam", string category = "IEEE") => new()
    {
        ReferenceNumber = 1, Title = "Towards multi-agentic AI", Year = 2025, VenueType = venueType, VenueName = venue, Category = category,
        Authors = new List<string> { author }, Doi = doi, Url = "https://ieeexplore.ieee.org/document/11334378/", Details = details,
    };

    [Fact]
    public void AnArticleGetsVolumeNumberAndPagesAndNoLandingPageBesideItsDoi()
    {
        string bib = BibliographyExporter.ToBibTeX(new[] { Row("Journals", "IEEE Access", "10.1109/ACCESS.2025.1", new ReferenceDetails { Volume = "13", Issue = "2", Pages = "10–20" }) });

        Assert.Contains("volume = {13}", bib);
        Assert.Contains("number = {2}", bib);
        Assert.Contains("pages = {10--20}", bib);
        Assert.DoesNotContain("url = ", bib);
    }

    [Fact]
    public void AProceedingsPaperNamesItsPublisher()
    {
        string bib = BibliographyExporter.ToBibTeX(new[] { Row("Conferences", "2025 ASEW", "10.1109/ASEW67777.2025.00063", new ReferenceDetails { Pages = "421–424", Publisher = "IEEE" }) });

        Assert.Contains("@inproceedings{", bib);
        Assert.Contains("publisher = {IEEE}", bib);
    }

    [Fact]
    public void AnArxivPreprintIsAMiscEntryWithItsEprint()
    {
        string bib = BibliographyExporter.ToBibTeX(new[] { Row("Preprints", "", "10.48550/arXiv.2607.02703", new ReferenceDetails { ArxivId = "2607.02703" }, category: "Arxiv") });
        string ris = BibliographyExporter.ToRis(new[] { Row("Preprints", "", "10.48550/arXiv.2607.02703", new ReferenceDetails { ArxivId = "2607.02703" }, category: "Arxiv") });

        Assert.Contains("@misc{", bib);
        Assert.Contains("eprint = {2607.02703}", bib);
        Assert.Contains("archiveprefix = {arXiv}", bib);
        Assert.Contains("TY  - UNPB", ris);
        Assert.Contains("M1  - arXiv:2607.02703", ris);
    }

    [Fact]
    public void RisSplitsThePagesAndLinksTheDoi()
    {
        string ris = BibliographyExporter.ToRis(new[] { Row("Journals", "IEEE Access", "10.1109/ACCESS.2025.1", new ReferenceDetails { Volume = "13", Issue = "2", Pages = "10–20" }) });

        Assert.Contains("VL  - 13", ris);
        Assert.Contains("IS  - 2", ris);
        Assert.Contains("SP  - 10", ris);
        Assert.Contains("EP  - 20", ris);
        Assert.Contains("UR  - https://doi.org/10.1109/ACCESS.2025.1", ris);
        Assert.DoesNotContain("ieeexplore", ris);
    }

    [Fact]
    public void AGroupAuthorIsBracedSoBibTexKeepsItWhole()
    {
        string bib = BibliographyExporter.ToBibTeX(new[] { Row("Journals", "Finance", null, null, author: "PSBC LLM Team") });

        Assert.Contains("author = {{PSBC LLM Team}}", bib);
    }

    [Fact]
    public void ATitleInCapitalsIsSetAsInTheReferenceList()
    {
        var row = Row("Journals", "IJETRM", null, null);
        row.Title = "AGENTIC AI FRAMEWORKS FOR AUTONOMOUS AUTOMOTIVE SOFTWARE VALIDATION";

        Assert.Contains("title = {{Agentic AI frameworks for autonomous automotive software validation}}", BibliographyExporter.ToBibTeX(new[] { row }));
        Assert.Contains("TI  - Agentic AI frameworks for autonomous automotive software validation", BibliographyExporter.ToRis(new[] { row }));
    }
}

using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A gallery version's DOI: how it is checked, where the citations point, and the ScholarlyArticle data.</summary>
public class GalleryDoiCitationTests
{
    private const string Base = "https://example.org";

    private static GalleryEntry Entry(string? doi = null, string title = "AI tutors in secondary schools") => new()
    {
        Id = "0123456789ab",
        RunId = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001"),
        Card = ReviewMethod.SystematicKey,
        Title = title,
        CompletedUtc = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc),
        ToolVersion = "0.5.1+abcdef1234567",
        Version = 2,
        Doi = doi,
    };

    [Theory]
    [InlineData("10.5281/zenodo.1234567", "10.5281/zenodo.1234567")]
    [InlineData(" doi:10.5281/zenodo.1234567 ", "10.5281/zenodo.1234567")]
    [InlineData("https://doi.org/10.17605/OSF.IO/ABCDE", "10.17605/OSF.IO/ABCDE")]
    [InlineData("not a doi", null)]
    [InlineData("10.1/short-registrant", null)]
    [InlineData("10.5281/a\"><script>", null)]
    [InlineData("10.5281/zenodo.1}, note={x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyThePlainFormOfADoiIsAccepted(string? value, string? expected) =>
        Assert.Equal(expected, GalleryDoi.Normalize(value));

    [Fact]
    public void WithADoiBothFormatsUseTheDoiLandingAddress()
    {
        var e = Entry("10.5281/zenodo.1234567");

        Assert.EndsWith(". https://doi.org/10.5281/zenodo.1234567", GalleryCitation.Apa(e, Base));
        Assert.DoesNotContain("/gallery/", GalleryCitation.Apa(e, Base));
        string bib = GalleryCitation.BibTex(e, Base);
        Assert.Contains(@"howpublished = {\url{https://doi.org/10.5281/zenodo.1234567}},", bib);
        Assert.Contains("doi          = {10.5281/zenodo.1234567},", bib);
        Assert.DoesNotContain("/gallery/", bib);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public void WithoutAValidDoiBothFormatsUseTheSiteAddress(string? doi)
    {
        var e = Entry(doi);

        Assert.EndsWith(". https://example.org/gallery/0123456789ab/v/2", GalleryCitation.Apa(e, Base));
        string bib = GalleryCitation.BibTex(e, Base);
        Assert.Contains(@"howpublished = {\url{https://example.org/gallery/0123456789ab/v/2}},", bib);
        Assert.DoesNotContain("doi ", bib);
        Assert.DoesNotContain("\n\n", bib);
    }

    [Fact]
    public void ADoiTravelsWithTheVersionThatHoldsIt()
    {
        var v = new GalleryVersion { Number = 3, RunId = Guid.NewGuid(), Doi = "10.5281/zenodo.99999" };
        Assert.Equal("10.5281/zenodo.99999", new GalleryEntry { Versions = new() { v } }.ForVersion(v).Doi);
    }

    [Fact]
    public void TheScholarlyArticleDataIsValidJsonBuiltFromTheVersion()
    {
        string json = GalleryStructuredData.ScholarlyArticle(Entry("10.5281/zenodo.1234567", "Costs <b> & \"quotes\"</script>"), Base);

        Assert.DoesNotContain("<", json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ScholarlyArticle", root.GetProperty("@type").GetString());
        Assert.Equal("Costs <b> & \"quotes\"</script>", root.GetProperty("headline").GetString());
        Assert.Equal("2026-11-24", root.GetProperty("datePublished").GetString());
        Assert.Equal("2", root.GetProperty("version").GetString());
        Assert.Equal(Base + "/gallery/0123456789ab/v/2", root.GetProperty("url").GetString());
        Assert.Equal(ArticleInfo.Author, root.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(Base + "/gallery/0123456789ab", root.GetProperty("isPartOf").GetProperty("url").GetString());
        Assert.Equal("10.5281/zenodo.1234567", root.GetProperty("identifier").GetProperty("value").GetString());
        Assert.Equal("https://doi.org/10.5281/zenodo.1234567", root.GetProperty("sameAs").GetString());
    }

    [Fact]
    public void TheScholarlyArticleDataLeavesTheDoiOutWhenThereIsNone()
    {
        using var doc = JsonDocument.Parse(GalleryStructuredData.ScholarlyArticle(Entry(), Base));
        Assert.False(doc.RootElement.TryGetProperty("identifier", out _));
        Assert.False(doc.RootElement.TryGetProperty("sameAs", out _));
    }
}

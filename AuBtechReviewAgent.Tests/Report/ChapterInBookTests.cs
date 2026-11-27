using System.Collections.Generic;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A chapter is cited in its book, with editors and publisher, not in the book series (issue #678, run 8942b961 [2]).</summary>
public class ChapterInBookTests
{
    // The shape of Crossref's answer for 10.4018/979-8-3373-7006-4.ch006: the series first, the book last, no editors.
    private const string Chapter = """
        {"status":"ok","message":{"type":"book-chapter","title":["Agentic Control Towers"],
         "author":[{"given":"Abdelrehim","family":"Awad"},{"given":"Dhafer","family":"Alahmari"}],
         "container-title":["Advances in Computational Intelligence and Robotics","Accelerating Logistics Through Generative AI, Digital Twins, and Autonomous Operations"],
         "page":"161-182","publisher":"IGI Global Scientific Publishing"}}
        """;

    private const string WithEditors = """
        {"status":"ok","message":{"type":"book-chapter","title":["Agents in the loop"],
         "container-title":["Studies in Computational Intelligence","Multi-Agent Systems Today"],
         "editor":[{"given":"Ann","family":"Lee"},{"given":"Jean-Paul","family":"Martin"}],"page":"1-20","publisher":"Springer"}}
        """;

    private static ScreeningLog Log() => new(
        "OPENALEX:W1", "Agentic control towers: Multi agent LLM RL orchestration for autonomous and resilient supply chains", "Included", "r", "c", "s",
        VenueType: ApaCitationBuilder.BookChapters, VenueName: "Advances in computational intelligence and robotics book series", Year: 2025,
        Authors: new List<string> { "Abdelrehim Awad", "Dhafer Alahmari" }, Doi: "10.4018/979-8-3373-7006-4.ch006");

    [Fact]
    public void CrossrefGivesAChaptersBookNotItsSeries()
    {
        var a = ReferenceAnswers.FromCrossref(Chapter)!;

        Assert.Equal("Accelerating Logistics Through Generative AI, Digital Twins, and Autonomous Operations", a.Venue);
        Assert.Equal(a.Venue, a.Details.Book);
        Assert.Equal("IGI Global Scientific Publishing", a.Details.Publisher);
        Assert.Null(a.Details.Editors);
        Assert.Equal(new[] { "Lee, Ann", "Martin, Jean-Paul" }, ReferenceAnswers.FromCrossref(WithEditors)!.Details.Editors);
    }

    [Fact]
    public void TheBookReplacesTheSeriesAndTheChapterIsCitedInIt()
    {
        var filled = new Dictionary<string, string>();
        var merged = ReferenceMerge.Merge(Log(), new[] { ReferenceAnswers.FromCrossref(Chapter)! }, filled);

        Assert.Equal("Accelerating Logistics Through Generative AI, Digital Twins, and Autonomous Operations", merged.VenueName);
        Assert.Equal(ApaCitationBuilder.BookChapters, merged.VenueType);
        Assert.Equal("Crossref", filled["venue"]);
        Assert.Contains(
            "In *Accelerating logistics through generative AI, digital twins, and autonomous operations* (pp. 161–182). IGI Global Scientific Publishing.",
            merged.ApaCitation);
        Assert.DoesNotContain("book series", merged.ApaCitation);
    }

    [Fact]
    public void EditorsStandBeforeTheBookWithInitialsFirst()
    {
        var paper = new AcademicPaper("x", "Agents in the loop", "", "Published: 2024", new List<string> { "Bo Chen" }, "Studies in Computational Intelligence", null, null);
        var details = ReferenceAnswers.FromCrossref(WithEditors)!.Details;

        var result = ApaCitationBuilder.Build(paper, details);

        Assert.Contains("In A. Lee & J.-P. Martin (Eds.), *Multi-agent systems today* (pp. 1–20). Springer.", result.Citation);
        Assert.Equal(ApaCitationBuilder.BookChapters, result.VenueType);
    }

    [Fact]
    public void BibTeXGivesTheBookTheEditorsAndThePublisher()
    {
        var row = new IncludedPaperMetricRow
        {
            ReferenceNumber = 1, Title = "Agents in the loop", Year = 2024, Category = "OpenAlex", VenueType = ApaCitationBuilder.BookChapters,
            VenueName = "Multi-Agent Systems Today", Authors = new List<string> { "Chen, Bo" },
            Details = ReferenceAnswers.FromCrossref(WithEditors)!.Details,
        };

        string bib = BibliographyExporter.ToBibTeX(new[] { row });

        Assert.Contains("@incollection{", bib);
        Assert.Contains("booktitle = {Multi-Agent Systems Today}", bib);
        Assert.Contains("editor = {", bib);
        Assert.Contains("publisher = {Springer}", bib);
    }
}

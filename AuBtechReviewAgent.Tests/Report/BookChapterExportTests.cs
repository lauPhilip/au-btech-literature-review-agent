using System.Collections.Generic;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A book chapter is not exported as a journal article, and the venue pie names it (issue #677).</summary>
public class BookChapterExportTests
{
    [Fact]
    public void A_book_chapter_is_exported_as_incollection()
    {
        var row = new IncludedPaperMetricRow
        {
            ReferenceNumber = 1, Title = "Fault tolerance in robots", Year = 2024, Category = "OpenAlex", VenueType = ApaCitationBuilder.BookChapters,
            VenueName = "Advances in computational intelligence and robotics book series", Authors = new List<string> { "Lee, Ann" },
        };
        string bib = BibliographyExporter.ToBibTeX(new[] { row });
        Assert.Contains("@incollection{", bib);
        Assert.DoesNotContain("@article{", bib);
        Assert.Contains("booktitle = {Advances in computational intelligence and robotics book series}", bib);
    }
}

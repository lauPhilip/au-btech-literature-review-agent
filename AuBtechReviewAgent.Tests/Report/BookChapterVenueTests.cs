using System.Collections.Generic;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A chapter in a book series is a book chapter, not a journal article (issue #677, run 8942b961).</summary>
public class BookChapterVenueTests
{
    private static IncludedPaperMetricRow Row(int n, string venueType) => new() { ReferenceNumber = n, Year = 2025, Category = "OpenAlex", VenueType = venueType };

    [Fact]
    public void A_book_series_venue_is_a_book_chapter_not_a_journal()
    {
        var paper = new AcademicPaper("OPENALEX:1", "Fault tolerance in robots", "abstract", "Published: 2024", new List<string> { "Ann Lee" },
            "Advances in computational intelligence and robotics book series", "10.4018/979-8-3693-0000-0.ch007", null);
        Assert.Equal(ApaCitationBuilder.BookChapters, ApaCitationBuilder.Build(paper).VenueType);

        var journal = new AcademicPaper("OPENALEX:2", "Fault tolerance", "abstract", "Published: 2024", new List<string> { "Ann Lee" },
            "Journal of Agent Systems", "10.1000/x", null);
        Assert.Equal("Journals", ApaCitationBuilder.Build(journal).VenueType);
    }

    [Fact]
    public void The_venue_sentence_counts_a_book_chapter_apart_from_the_journals()
    {
        string text = MethodsSectionWriter.IncludedSet(new[] { Row(1, "Journals"), Row(2, ApaCitationBuilder.BookChapters), Row(3, "Journals"), Row(4, "Preprints") });
        Assert.Contains("By venue type: 2 journal, 0 transactions, 0 conference, 1 book chapter and 1 preprint or unknown.", text);
        // Without one, the sentence reads as it always did.
        Assert.Contains("By venue type: 1 journal, 0 transactions, 0 conference and 1 preprint or unknown.",
            MethodsSectionWriter.IncludedSet(new[] { Row(1, "Journals"), Row(4, "Preprints") }));
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The references of a multivocal run (MLR G-6), built in code from the search records and the page snapshots:
/// numbered as in the source repository, with the access date and the Wayback Machine capture, and exported as
/// BibTeX, RIS and a screened RIS with every decision.
/// </summary>
public class GreyReferencesTests
{
    private static GreyRecord Record(string url, string title, string producer, string site, string kind, DateTime? published) =>
        new("x:" + url, title, "", url, producer, site, kind, published, new Dictionary<string, long>());

    private static (MultivocalLedger Ledger, MultivocalPagesFile Pages, MultivocalExtractionFile Extraction, MultivocalScreeningFile Screening) Run()
    {
        var ledger = new MultivocalLedger
        {
            Sources =
            {
                new FoundGreySource { Record = Record("https://blog.example.org/post", "Managing agent context", "Ana Berg", "blog.example.org", "blogs", new DateTime(2025, 5, 1)) },
                new FoundGreySource { Record = Record("https://docs.acme.dev/guide", "Effective context & memory_management", "", "docs.acme.dev", "documentation", null) },
                new FoundGreySource { Record = Record("https://forum.example.org/t/1", "Why agents forget", "Bo", "forum.example.org", "qa", null) },
                new FoundGreySource { Record = Record("https://news.example.org/x", "Cooking with agents", "Cy", "news.example.org", "news", null) },
            },
        };
        var pages = new MultivocalPagesFile
        {
            Pages =
            {
                new PageSnapshot { Url = "https://blog.example.org/post", AccessedUtc = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc), WaybackUrl = "https://web.archive.org/web/2026/https://blog.example.org/post" },
                new PageSnapshot { Url = "https://docs.acme.dev/guide", AccessedUtc = new DateTime(2026, 10, 7), NotKept = "robots.txt does not allow it", WaybackUrl = "javascript:alert(1)" },
            },
        };
        // The repository order is the extraction's: the guide is source 1, the blog post source 2; the forum post failed.
        var extraction = new MultivocalExtractionFile
        {
            Sources =
            {
                new GreyExtraction { Address = "docs.acme.dev/guide", Title = "Effective context & memory_management", Url = "https://docs.acme.dev/guide" },
                new GreyExtraction { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post" },
                new GreyExtraction { Address = "forum.example.org/t/1", Title = "Why agents forget", Url = "https://forum.example.org/t/1", Error = "The model call failed." },
            },
        };
        var screening = new MultivocalScreeningFile
        {
            Sources =
            {
                new GreyScreening { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post", Decision = "Included", Uncertain = true, Reviewed = true },
                new GreyScreening { Address = "docs.acme.dev/guide", Title = "Effective context & memory_management", Url = "https://docs.acme.dev/guide", Decision = "Included", Uncertain = true },
                new GreyScreening { Address = "news.example.org/x", Title = "Cooking with agents", Url = "https://news.example.org/x", Decision = "Excluded", ExclusionReason = ExclusionReasons.OffTopic, Reasoning = "About\ncooking." },
                new GreyScreening { Address = "forum.example.org/t/1", Title = "Why agents forget", Url = "https://forum.example.org/t/1", Decision = "Duplicate" },
            },
        };
        return (ledger, pages, extraction, screening);
    }

    [Fact]
    public void ReferencesFollowTheSourceRepositoryAndCarryTheAccessDateAndArchive()
    {
        var (ledger, pages, extraction, _) = Run();

        var references = GreyReferences.Build(ledger, pages, extraction);

        Assert.Equal(new[] { 1, 2 }, references.Select(r => r.Number)); // the source that could not be extracted is not cited
        var guide = references[0];
        Assert.Equal(("docs.acme.dev", (int?)null), (guide.Producer, guide.Year)); // no producer: credited to the site
        Assert.Null(guide.AccessedUtc); // its page was not kept
        Assert.Null(guide.WaybackUrl); // and an address that is not http(s) is never linked
        Assert.Equal("docs.acme.dev (n.d.). Effective context & memory_management. https://docs.acme.dev/guide", GreyReferences.Line(guide));
        Assert.Equal("Ana Berg (2025). Managing agent context. *blog.example.org*. https://blog.example.org/post (accessed 2026-10-07; archived at https://web.archive.org/web/2026/https://blog.example.org/post)",
            GreyReferences.Line(references[1]));
        Assert.Equal(MultivocalReporter.SourceNumbers(extraction)["blog.example.org/post"], references[1].Number); // the number the findings carry
    }

    [Fact]
    public void BibTeXAndRisCanBeImportedIntoAReferenceManager()
    {
        var (ledger, pages, extraction, _) = Run();
        var references = GreyReferences.Build(ledger, pages, extraction);

        string bib = GreyReferences.ToBibTeX(references);
        Assert.Contains("@misc{docsndeffective,", bib);
        Assert.Contains("title = {{Effective context \\& memory\\_management}}", bib);
        Assert.Contains("author = {{Ana Berg}}", bib); // kept as one name, not split into first and last
        Assert.Contains("urldate = {2026-10-07}", bib);
        Assert.Contains("note = {Grey literature (Blog posts and practitioner articles); source [2] in the TraceableAI review; archived at https://web.archive.org/web/2026/https://blog.example.org/post}", bib);
        Assert.DoesNotContain("javascript:", bib);

        string ris = GreyReferences.ToRis(references);
        Assert.Equal(2, ris.Split("TY  - ELEC").Length - 1);
        Assert.Contains("Y2  - 2026/10/07", ris);
        Assert.Contains("N1  - Source [1] in the TraceableAI review", ris);
        Assert.Contains("KW  - grey literature", ris);
    }

    [Fact]
    public void TheScreenedFileHoldsEverySourceWithItsDecision()
    {
        var (ledger, _, extraction, screening) = Run();

        string ris = GreyReferences.ToScreenedRis(ledger, screening, MultivocalReporter.SourceNumbers(extraction));

        var records = ris.Split("ER  - ", StringSplitOptions.RemoveEmptyEntries).Where(r => r.Contains("TY  -")).ToList();
        Assert.Equal(4, records.Count);
        Assert.Contains("KW  - checked by reviewer", records[0]);
        Assert.DoesNotContain("marked to look at", records[0]); // the reviewer has looked
        Assert.Contains("N1  - Source [2] in the TraceableAI review", records[0]);
        Assert.Contains("KW  - marked to look at", records[1]);
        Assert.Contains("KW  - excluded: off topic", records[2]);
        Assert.Contains("N1  - Screening: About cooking.", records[2]); // on one line
        Assert.Contains("KW  - duplicate", records[3]);
        Assert.DoesNotContain("Source [", records[3]);
    }
}

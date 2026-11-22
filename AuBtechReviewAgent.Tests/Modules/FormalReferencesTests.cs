using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The references of a review with both pools (block I-6): a formal source is cited as the systematic review cites a
/// study, a source resting on its abstract alone says so, and the grey sources keep their own references.
/// </summary>
public class FormalReferencesTests
{
    private static (MultivocalLedger, MultivocalPagesFile, MultivocalExtractionFile, MultivocalScreeningFile) Run()
    {
        var (ledger, pages, extraction, screening) = GreyReferencesPinTests.Run();
        var full = new AcademicPaper("OPENALEX_W1", "Context summaries for LLM agents", "An abstract.", "2025-03-01", new() { "Jane Doe", "Kim Lee" }, "Journal of Agents", "10.1234/agents.1");
        var onAbstract = new AcademicPaper("OPENALEX_W4", "Memory hierarchies in agent frameworks", "Agents page old turns out.", "2024-11-05", new() { "Ola Nordmann" }, "Proceedings of AgentConf", null, "https://example.org/papers/w4");
        ledger.FormalRemoved = new();
        foreach (var (paper, url) in new[] { (full, "https://doi.org/10.1234/agents.1"), (onAbstract, "https://example.org/papers/w4") })
        {
            ledger.Sources.Add(new FoundGreySource { Record = new GreyRecord(paper.Id, paper.Title, paper.Abstract, url, string.Join(", ", paper.Authors), paper.JournalSource, MultivocalSearcher.FormalKind, null, new Dictionary<string, long>()), Paper = paper });
            string address = MultivocalSearcher.AddressKey(url);
            pages.Pages.Add(new PageSnapshot { Url = url, AccessedUtc = new DateTime(2026, 11, 1), TextBasis = paper == full ? MultivocalPages.FullTextBasis : MultivocalPages.AbstractBasis });
            extraction.Sources.Add(new GreyExtraction { Address = address, Title = paper.Title, Url = url, Formal = true });
            screening.Sources.Add(new GreyScreening { Address = address, Title = paper.Title, Url = url, Decision = "Included", Formal = true, Uncertain = true });
        }
        return (ledger, pages, extraction, screening);
    }

    [Fact]
    public void AFormalSourceIsCitedAsTheSystematicReviewCitesAStudy()
    {
        var (ledger, pages, extraction, _) = Run();
        var references = GreyReferences.Build(ledger, pages, extraction);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, references.Select(r => r.Number));
        Assert.Equal(ApaCitationBuilder.Build(ledger.FormalPool.First().Paper!).Citation, GreyReferences.Line(references[3]));
        Assert.StartsWith("Doe, J., & Lee, K. (2025). Context summaries for LLM agents. *Journal of Agents*.", GreyReferences.Line(references[3]));
        Assert.EndsWith($"({GreyReferences.AbstractOnlyNote})", GreyReferences.Line(references[4]));
        Assert.DoesNotContain("abstract only", GreyReferences.Line(references[3]));
    }

    [Fact]
    public void TheExportsHoldBothPoolsAndMarkTheAbstractOnlySource()
    {
        var (ledger, pages, extraction, screening) = Run();
        var references = GreyReferences.Build(ledger, pages, extraction);

        string bib = GreyReferences.ToBibTeX(references);
        Assert.Contains("@article{doe2025context,", bib);
        Assert.Contains("doi = {10.1234/agents.1}", bib);
        Assert.Contains("@inproceedings{nordmann2024memory,", bib);
        Assert.Contains($"reference [5]; {GreyReferences.AbstractOnlyNote}}}", bib);
        Assert.Contains("@misc{ana2025managing,", bib); // the grey entries as before

        string ris = GreyReferences.ToRis(references);
        Assert.Contains("TY  - JOUR", ris);
        Assert.Contains("N1  - Abstract only: no open full text was found", ris);
        Assert.Equal(2, ris.Split("KW  - formal literature").Length - 1);
        Assert.Equal(3, ris.Split("KW  - grey literature").Length - 1);

        string screened = GreyReferences.ToScreenedRis(ledger, screening, MultivocalReporter.SourceNumbers(extraction));
        Assert.Contains("TY  - CONF", screened);
        Assert.Contains("KW  - formal literature\nKW  - included\nKW  - marked to look at\nN1  - Source [4] in the TraceableAI review", screened.Replace("\r\n", "\n"));
    }
}

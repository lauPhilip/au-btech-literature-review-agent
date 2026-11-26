using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Singular and plural in the methods written in code (issue #677, run 8942b961).</summary>
public class MethodsGrammarTests
{
    [Fact]
    public void One_screening_error_is_singular()
    {
        string one = MethodsSectionWriter.SelectionProcess(false, new ReviewStats { ScreeningErrors = 1 });
        Assert.Contains("1 record could not be screened because the model call failed; it is listed in the run ledger.", one);
        string two = MethodsSectionWriter.SelectionProcess(false, new ReviewStats { ScreeningErrors = 2 });
        Assert.Contains("2 records could not be screened because the model call failed; they are listed in the run ledger.", two);
    }

    [Fact]
    public void Two_sources_are_both_sources()
    {
        string text = MethodsSectionWriter.SearchStrategy("q1", new[] { "q1", "q2" }, 10, 0, 0,
            yields: new[] { new SearchStringYield("q1", 2, 20, 20), new SearchStringYield("q2", 2, 20, 5) });
        Assert.Contains("\"q2\" was run against both sources", text);
        string three = MethodsSectionWriter.SearchStrategy("q1", new[] { "q1", "q2" }, 10, 0, 0,
            yields: new[] { new SearchStringYield("q1", 3, 30, 30), new SearchStringYield("q2", 3, 30, 5) });
        Assert.Contains("run against all 3 sources", three);
    }

    [Fact]
    public void Two_references_skipped_for_coming_from_arXiv_say_they_came()
    {
        var audit = new ReferenceEnrichmentFile();
        audit.References.Add(new ReferenceEnrichmentEntry
        {
            PaperId = "p", Lookups = { new ReferenceLookupLog { Service = ReferenceAnswers.Crossref, Url = "https://example.org", Status = "fetched" } },
        });
        for (int i = 0; i < 2; i++)
            audit.References.Add(new ReferenceEnrichmentEntry { PaperId = "a" + i, Skipped = ReferenceEnrichmentText.ArxivNoVenue });
        string text = ReferenceEnrichmentText.Sentence(audit, "included study", "included studies");
        Assert.Contains("The other 2 were not looked up: 2 because they came from arXiv, which gives the full author list, and their arXiv entries name no journal or proceedings to complete them from.", text);

        audit.References.RemoveAt(2);
        Assert.Contains("The other one was not looked up: 1 because it came from arXiv", ReferenceEnrichmentText.Sentence(audit, "included study", "included studies"));
    }
}

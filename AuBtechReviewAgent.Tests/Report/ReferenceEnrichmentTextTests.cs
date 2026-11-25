using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The methods sentence on the reference enrichment, written in code from reference-enrichment.json.</summary>
public class ReferenceEnrichmentTextTests
{
    private static ReferenceEnrichmentEntry Entry(string service, params string[] filled) => new()
    {
        PaperId = "p",
        Lookups = { new ReferenceLookupLog { Service = service, Url = "https://example.org", Status = "fetched" } },
        Filled = filled.ToDictionary(f => f, _ => service),
    };

    [Fact]
    public void TheSentenceCountsTheReferencesThatGainedEachFieldAndSaysNoModelWasInvolved()
    {
        var audit = new ReferenceEnrichmentFile
        {
            References =
            {
                Entry(ReferenceAnswers.Crossref, "authors", "volume", "issue", "pages"),
                Entry(ReferenceAnswers.Crossref, "volume", "publisher"),
                Entry(ReferenceAnswers.Arxiv, "authors"),
                Entry(ReferenceAnswers.OpenAlex),
            },
        };

        string sentence = ReferenceEnrichmentText.Sentence(audit, "included study", "included studies");

        Assert.Equal(
            "Before the references were numbered, the references of the 4 included studies with a DOI or arXiv number were looked up " +
            "in code at Crossref, OpenAlex and arXiv by that identifier, and 3 of them gained at least one field (2 the full author list, " +
            "2 the volume, 1 the issue, 1 the pages, 1 the publisher). No language model was involved: an answer was used only when its " +
            "title named the same work, it filled gaps without changing the year, title or venue the source gave, and every lookup and " +
            "filled field is listed in reference-enrichment.json.", sentence);
    }

    [Fact]
    public void OneReferenceIsNamedInTheSingularAndNothingLookedUpGivesNoSentence()
    {
        var one = new ReferenceEnrichmentFile { References = { Entry(ReferenceAnswers.Crossref, "authors", "pages") } };
        Assert.Contains("the reference of the one academic source with a DOI or arXiv number was looked up in code at Crossref by that identifier, and it gained the full author list and the pages.",
            ReferenceEnrichmentText.Sentence(one, "academic source", "academic sources"));

        var none = new ReferenceEnrichmentFile { References = { Entry(ReferenceAnswers.Crossref), Entry(ReferenceAnswers.Crossref) } };
        Assert.Contains("and none of them gained a field.", ReferenceEnrichmentText.Sentence(none, "included study", "included studies"));

        Assert.Equal("", ReferenceEnrichmentText.Sentence(new ReferenceEnrichmentFile(), "included study", "included studies"));
        Assert.Equal("", ReferenceEnrichmentText.Sentence(null, "included study", "included studies"));
    }
}

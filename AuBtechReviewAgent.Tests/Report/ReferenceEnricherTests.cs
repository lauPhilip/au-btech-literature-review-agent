using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The reference enrichment: one request per DOI, answers saved and reused, gaps filled, other works refused.</summary>
public class ReferenceEnricherTests : IDisposable
{
    private readonly string _run = Path.Join(Path.GetTempPath(), "enrich-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _asked = new();

    public void Dispose() => TestFolders.TryDelete(_run);

    private Func<string, Task<string>> Answers(Dictionary<string, string> byHost) => url =>
    {
        _asked.Add(url);
        foreach (var (key, body) in byHost) if (url.Contains(key)) return Task.FromResult(body);
        throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
    };

    private static ScreeningLog Log(string id, string title, List<string> authors, string venue, string? doi, ReferenceDetails? details = null) =>
        new(id, title, "Included", "fits", "OLD", "", "Journals", venue, 2026, authors, doi, null, Details: details);

    [Fact]
    public async Task AScopusFirstAuthorBecomesTheFullListWithNamesFromTheirParts()
    {
        var enricher = new ReferenceEnricher(Answers(new() { ["api.crossref.org/works/10.1145/3796507"] = ReferenceAnswersTests.CrossrefJson }), "team@example.org");
        var scopus = Log("SCOPUS_ID:1", "Large language model-based agents for software engineering: A survey", new() { "Jun-wei L.", ApaCitationBuilder.EtAl },
            "ACM Transactions on Software Engineering and Methodology", "10.1145/3796507");

        var (logs, audit) = await enricher.EnrichAsync(new[] { scopus }, _run);

        Assert.Equal("Liu, J., Wang, K., & SE Agents Consortium (2026). Large language model-based agents for software engineering: A survey. *ACM Transactions on Software Engineering and Methodology*, *35*(2), 1–79. https://doi.org/10.1145/3796507", logs[0].ApaCitation);
        var entry = Assert.Single(audit.References);
        Assert.Equal("Crossref", entry.Filled["authors"]);
        Assert.Equal("Crossref", logs[0].Details!.EnrichedFrom!["volume"]);
        Assert.Equal("OLD", entry.Before);
        // The completed fields are on file, so the multivocal review can cite them without changing its ledger.
        Assert.Equal(logs[0].Authors, entry.Completed!.Authors);
        Assert.Equal("ACM Transactions on Software Engineering and Methodology", entry.Completed.Venue);
        Assert.Equal("35", entry.Completed.Details!.Volume);
        var lookup = Assert.Single(entry.Lookups);
        Assert.DoesNotContain("mailto", lookup.Url);
        Assert.Contains("mailto=team%40example.org", Assert.Single(_asked));
        byte[] saved = File.ReadAllBytes(Path.Join(_run, lookup.RawFile!));
        Assert.Equal(RunManifest.Sha256(saved), lookup.Sha256);
    }

    [Fact]
    public async Task TheSameRunAsksEachDoiOnceAndReusesTheSavedAnswer()
    {
        var get = Answers(new() { ["api.crossref.org"] = ReferenceAnswersTests.CrossrefJson });
        var log = Log("S2_1", "Large Language Model-Based Agents for Software Engineering", new() { "Junwei Liu" }, "", "https://doi.org/10.1145/3796507");

        await new ReferenceEnricher(get).EnrichAsync(new[] { log }, _run);
        var (logs, audit) = await new ReferenceEnricher(get).EnrichAsync(new[] { log }, _run);

        Assert.Single(_asked);
        Assert.Equal("saved answer reused", audit.References[0].Lookups[0].Status);
        Assert.Equal("ACM Transactions on Software Engineering and Methodology", logs[0].VenueName);
    }

    [Fact]
    public async Task ADoiCrossrefDoesNotKnowIsAskedOfOpenAlex()
    {
        var enricher = new ReferenceEnricher(Answers(new() { ["api.openalex.org/works/doi:10.5281"] = ReferenceAnswersTests.OpenAlexJson }));
        var log = Log("OPENALEX_W7160705205", "AGENTIC AI FRAMEWORKS FOR AUTONOMOUS AUTOMOTIVE SOFTWARE VALIDATION", new() { "Abhishek Devgan" },
            "International Journal of Engineering Technology Research & Management", "10.5281/zenodo.20096249");

        var (logs, audit) = await enricher.EnrichAsync(new[] { log }, _run);

        Assert.Equal(new[] { "not found", "fetched" }, audit.References[0].Lookups.Select(l => l.Status));
        Assert.Contains("*International Journal of Engineering Technology Research & Management*, *9*(5), 12–30.", logs[0].ApaCitation);
        Assert.Equal(new[] { "Abhishek Devgan" }, logs[0].Authors); // OpenAlex's string names do not replace an equal list
    }

    [Fact]
    public async Task AnArxivDoiFromAnotherDatabaseGetsItsAuthorsFromArxiv()
    {
        var enricher = new ReferenceEnricher(Answers(new() { ["export.arxiv.org/api/query?id_list=2607.02703"] = ReferenceAnswersTests.ArxivXml }));
        var log = Log("SCOPUS_ID:2", "LLMoxie: Exploring Agentic AI for Scientific Software Development", new() { "Setiawan L.", ApaCitationBuilder.EtAl }, "", "10.48550/arXiv.2607.02703");

        var (logs, _) = await enricher.EnrichAsync(new[] { log }, _run);

        Assert.StartsWith("Setiawan, L., & Mittal, A. (2026). *LLMoxie", logs[0].ApaCitation);
        Assert.DoesNotContain(_asked, u => u.Contains("crossref"));
    }

    [Fact]
    public async Task AnAnswerAboutAnotherWorkIsNotUsed()
    {
        var enricher = new ReferenceEnricher(Answers(new() { ["api.crossref.org"] = ReferenceAnswersTests.CrossrefJson }));
        var log = Log("S2_2", "Crop yields in wheat", new() { "Ola Nordmann" }, "Field Crops Research", "10.1145/3796507");

        var (logs, audit) = await enricher.EnrichAsync(new[] { log }, _run);

        Assert.Same(log, logs[0]);
        Assert.Contains("names another title", Assert.Single(audit.References[0].NotUsed));
    }

    [Fact]
    public async Task ARecordFromArxivOrWithoutAnIdentifierAsksNothing()
    {
        var enricher = new ReferenceEnricher(Answers(new()));
        var arxiv = Log("http://arxiv.org/abs/2607.02703v1", "LLMoxie", new() { "Landung Setiawan" }, "", "10.48550/arXiv.2607.02703");
        var bare = Log("SCHOLAR_1", "No identifier", new() { "Jane Doe" }, "Future Internet", null);

        var (_, audit) = await enricher.EnrichAsync(new[] { arxiv, bare }, _run);

        Assert.Empty(_asked);
        // Issue #566: the arXiv record is on file with the reason it was not looked up; the one without an identifier is not.
        var skipped = Assert.Single(audit.References);
        Assert.Equal(("http://arxiv.org/abs/2607.02703v1", 0), (skipped.PaperId, skipped.Lookups.Count));
        Assert.StartsWith("it came from arXiv", skipped.Skipped);
    }

    // Issue #566: run 289a0d3e said "the references of the 5 included studies with a DOI or arXiv number were looked up",
    // although all 7 had one; the two from arXiv were skipped without a word.
    [Fact]
    public async Task TheMethodsCountEveryStudyWithAnIdentifierAndExplainTheSkippedOnes()
    {
        var enricher = new ReferenceEnricher(Answers(new()));
        var (_, audit) = await enricher.EnrichAsync(new[]
        {
            Log("SCHOLAR_1", "A title", new() { "Jane Doe" }, "Future Internet", "10.3390/fi1"),
            Log("http://arxiv.org/abs/2601.00001v1", "A preprint", new() { "Jane Doe" }, "", "10.48550/arXiv.2601.00001"),
            Log("SCHOLAR_2", "No identifier", new() { "Jane Doe" }, "Field Crops Research", null),
        }, _run);

        Assert.Equal(
            "Before the references were numbered, the reference of 1 of the 2 included studies with a DOI or arXiv number was looked up " +
            "in code at Crossref and OpenAlex by that identifier, and it gained no field. The other one was not looked up: 1 because it came " +
            "from arXiv, which gives the full author list, and its arXiv entry names no journal or proceedings to complete it from. No language model " +
            "was involved: an answer was used only when its title named the same work, it filled gaps without changing the year, title or venue the " +
            "source gave, and every lookup and filled field is listed in reference-enrichment.json.",
            ReferenceEnrichmentText.Sentence(audit, "included study", "included studies"));
    }

    [Fact]
    public void TheMethodsNameAPublishedVersionFoundByTitle()
    {
        var entry = new ReferenceEnrichmentEntry
        {
            Lookups = { new ReferenceLookupLog { Service = ReferenceAnswers.Crossref, Url = "https://api.crossref.org/works?query.bibliographic=A%20title&rows=5", Status = "fetched" } },
            Filled = { ["doi"] = "Crossref", ["venue"] = "Crossref", ["year"] = "Crossref", ["volume"] = "Crossref" },
        };
        string sentence = ReferenceEnrichmentText.Sentence(new ReferenceEnrichmentFile { References = { entry } }, "included study", "included studies");
        Assert.Contains("Crossref was also searched by title for the published version of 1 included study from arXiv whose arXiv entry names a journal or proceedings, and 1 is cited as that published version, with its DOI, year and venue", sentence);
        Assert.Contains("(except for a published version, which replaces the preprint)", sentence);
    }
}

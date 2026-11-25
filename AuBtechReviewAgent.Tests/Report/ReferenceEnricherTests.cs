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
        var scopus = Log("SCOPUS_ID:1", "Large Language Model-Based Agents for Software Engineering: A Survey", new() { "Jun-wei L.", ApaCitationBuilder.EtAl },
            "ACM Transactions on Software Engineering and Methodology", "10.1145/3796507");

        var (logs, audit) = await enricher.EnrichAsync(new[] { scopus }, _run);

        Assert.Equal("Liu, J., Wang, K., & SE Agents Consortium (2026). Large Language Model-Based Agents for Software Engineering: A Survey. *ACM Transactions on Software Engineering and Methodology*, *35*(2), 1–79. https://doi.org/10.1145/3796507", logs[0].ApaCitation);
        var entry = Assert.Single(audit.References);
        Assert.Equal("Crossref", entry.Filled["authors"]);
        Assert.Equal("Crossref", logs[0].Details!.EnrichedFrom!["volume"]);
        Assert.Equal("OLD", entry.Before);
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
        Assert.Empty(audit.References);
    }
}

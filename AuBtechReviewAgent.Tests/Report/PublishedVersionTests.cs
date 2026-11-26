using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Issue #566: an arXiv preprint later published in a journal is cited as the journal article.</summary>
public class PublishedVersionTests : IDisposable
{
    private readonly string _run = Path.Join(Path.GetTempPath(), "published-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _asked = new();

    public void Dispose() => TestFolders.TryDelete(_run);

    private const string Title = "A survey of multi-agent deep reinforcement learning with communication";

    private static string Search(string type, string doi, string title = Title, string family = "Zhu") => $$$"""
        {"message":{"items":[
          {"DOI":"10.48550/arXiv.2203.08975","type":"posted-content","title":["{{{Title}}}"],"author":[{"family":"Zhu","given":"Changxi"}]},
          {"DOI":"{{{doi}}}","type":"{{{type}}}","title":["{{{title}}}"],"container-title":["Autonomous Agents and Multi-Agent Systems"],
           "volume":"38","issue":"1","article-number":"4","published":{"date-parts":[[2024,1,9]]},
           "author":[{"family":"{{{family}}}","given":"Changxi"},{"family":"Dastani","given":"Mehdi"},{"family":"Wang","given":"Shihan"}]}]}}
        """;

    private ReferenceEnricher Enricher(string? searchAnswer) => new(url =>
    {
        _asked.Add(url);
        return searchAnswer != null && url.Contains("query.bibliographic=") ? Task.FromResult(searchAnswer)
            : throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
    });

    // As the screening ledger holds reference [7] of run 289a0d3e.
    private static ScreeningLog Run289Reference7()
    {
        var paper = new AcademicPaper("http://arxiv.org/abs/2203.08975v2", Title, "", "Published: 2022", new() { "Changxi Zhu", "Mehdi Dastani", "Shihan Wang" },
            "Auton. Agents Multi Agent Syst. 38(1): 4 (2024)", "10.48550/arXiv.2203.08975", "https://arxiv.org/abs/2203.08975v2");
        var apa = ApaCitationBuilder.Build(paper);
        return new ScreeningLog(paper.Id, Title, "Included", "fits", apa.Citation, "", apa.VenueType, apa.VenueName, apa.Year, paper.Authors, paper.Doi, paper.Url, Details: apa.Details);
    }

    [Fact]
    public async Task ThePublishedJournalArticleIsCitedInsteadOfThePreprint()
    {
        var (logs, audit) = await Enricher(Search("journal-article", "10.1007/s10458-023-09633-6")).EnrichAsync(new[] { Run289Reference7() }, _run);

        var cited = Assert.Single(logs);
        Assert.Equal("Zhu, C., Dastani, M., & Wang, S. (2024). A survey of multi-agent deep reinforcement learning with communication. *Autonomous Agents and Multi-Agent Systems*, *38*(1), Article 4. https://doi.org/10.1007/s10458-023-09633-6", cited.ApaCitation);
        Assert.Equal(("Journals", 2024, "10.1007/s10458-023-09633-6"), (cited.VenueType, cited.Year, cited.Doi));
        Assert.Equal("2203.08975", cited.Details?.ArxivId);
        var entry = Assert.Single(audit.References);
        Assert.Contains("query.bibliographic=", Assert.Single(entry.Lookups).Url);
        Assert.Equal("Crossref", entry.Filled["doi"]);
        Assert.Equal("Crossref", entry.Filled["year"]);
    }

    [Theory]
    [InlineData("posted-content", "10.1007/x", Title, "Zhu")]
    [InlineData("journal-article", "10.1007/x", "A survey of single-agent learning", "Zhu")]
    [InlineData("journal-article", "10.1007/x", Title, "Nguyen")]
    public async Task AnotherWorkOrAPreprintIsNotTakenForThePublishedVersion(string type, string doi, string title, string family)
    {
        var (logs, audit) = await Enricher(Search(type, doi, title, family)).EnrichAsync(new[] { Run289Reference7() }, _run);

        var cited = Assert.Single(logs);
        Assert.Equal("10.48550/arXiv.2203.08975", cited.Doi);
        Assert.Contains("*Auton. Agents Multi Agent Syst.*, *38*(1), Article 4.", cited.ApaCitation);
        Assert.Contains(Assert.Single(audit.References).NotUsed, n => n.StartsWith("Crossref names no journal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APreprintWithoutAJournalReferenceIsNotSearchedFor()
    {
        var log = Run289Reference7() with { VenueName = "", VenueType = "Preprints" };
        await Enricher(null).EnrichAsync(new[] { log }, _run);
        Assert.Empty(_asked);
    }
}

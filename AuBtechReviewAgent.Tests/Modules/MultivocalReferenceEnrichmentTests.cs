using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The reference enrichment of a multivocal run's formal pool: the extracted academic sources are completed from the
/// same lookups as the systematic review's, the grey sources are not looked up, and the ledger keeps the database's record.
/// </summary>
public class MultivocalReferenceEnrichmentTests : IDisposable
{
    private readonly string _run = Path.Join(Path.GetTempPath(), "mlr-enrich-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _asked = new();

    public void Dispose() => TestFolders.TryDelete(_run);

    /// <summary>The pinned grey run with two formal sources: one with a DOI, one with an address alone.</summary>
    internal static (MultivocalLedger, MultivocalPagesFile, MultivocalExtractionFile) Run()
    {
        var (ledger, pages, extraction, _) = GreyReferencesPinTests.Run();
        var withDoi = new AcademicPaper("OPENALEX_W1", "Context summaries for LLM agents", "An abstract.", "2025-03-01", new() { "Jane Doe", "Kim Lee" }, "Journal of Agents", "10.1234/agents.1");
        var withAddress = new AcademicPaper("OPENALEX_W4", "Memory hierarchies in agent frameworks", "An abstract.", "2024-11-05", new() { "Ola Nordmann" }, "Proceedings of AgentConf", null, "https://example.org/papers/w4");
        ledger.FormalRemoved = new();
        foreach (var (paper, url) in new[] { (withDoi, "https://doi.org/10.1234/agents.1"), (withAddress, "https://example.org/papers/w4") })
        {
            ledger.Sources.Add(new FoundGreySource { Record = new GreyRecord(paper.Id, paper.Title, paper.Abstract, url, string.Join(", ", paper.Authors), paper.JournalSource, MultivocalSearcher.FormalKind, null, new Dictionary<string, long>()), Paper = paper });
            extraction.Sources.Add(new GreyExtraction { Address = MultivocalSearcher.AddressKey(url), Title = paper.Title, Url = url, Formal = true });
        }
        return (ledger, pages, extraction);
    }

    private static string Crossref(string title) => $$$"""
        {"status":"ok","message":{"DOI":"10.1234/agents.1","type":"journal-article","title":["{{{title}}}"],
         "author":[{"given":"Jane","family":"Doe"},{"given":"Kim","family":"Lee"},{"given":"Ann","family":"Park"}],
         "container-title":["Journal of Agents"],"volume":"7","issue":"2","page":"10-20","publisher":"Agent Press"}}
        """;

    private Func<string, Task<string>> Answers(string body) => url =>
    {
        _asked.Add(url);
        return url.Contains("api.crossref.org/works/10.1234/agents.1") ? Task.FromResult(body)
            : throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
    };

    [Fact]
    public async Task TheFormalSourcesAreCompletedAndTheGreySourcesAreNotLookedUp()
    {
        var (ledger, pages, extraction) = Run();

        var audit = await MultivocalReferenceEnrichment.RunAsync(_run, ledger, extraction, Answers(Crossref("Context summaries for LLM agents")));

        // One lookup: the formal source with a DOI. The one without, and the three grey sources, are not asked about.
        Assert.Single(_asked);
        Assert.True(File.Exists(Path.Join(_run, ReferenceEnrichmentFile.FileName)));
        Assert.Equal(new[] { "authors", "issue", "pages", "volume" }, Assert.Single(audit!.References).Filled.Keys.Order());

        Assert.Same(audit, ledger.References);
        var references = GreyReferences.Build(ledger, pages, extraction);
        Assert.Equal("Doe, J., Lee, K., & Park, A. (2025). Context summaries for LLM agents. *Journal of Agents*, *7*(2), 10–20. https://doi.org/10.1234/agents.1",
            GreyReferences.Line(references[3]));
        Assert.Contains("volume = {7}", GreyReferences.ToBibTeX(references));
        Assert.Contains("SP  - 10", GreyReferences.ToRis(references));
        Assert.Equal(new[] { "Jane Doe", "Kim Lee" }, ledger.FormalPool.First().Paper!.Authors); // the ledger keeps the database's record
        ledger.References = null;
        Assert.Equal(GreyReferences.Build(ledger, pages, extraction).Take(3).Select(GreyReferences.Line), references.Take(3).Select(GreyReferences.Line)); // the grey references as before
    }

    [Fact]
    public async Task AnAnswerAboutAnotherWorkIsRefusedAndASecondRunReusesTheSavedAnswer()
    {
        var (ledger, pages, extraction) = Run();
        string before = GreyReferences.Line(GreyReferences.Build(ledger, pages, extraction)[3]);

        await MultivocalReferenceEnrichment.RunAsync(_run, ledger, extraction, Answers(Crossref("A different paper on agents")));
        var audit = await MultivocalReferenceEnrichment.RunAsync(_run, ledger, extraction, Answers(Crossref("A different paper on agents")));

        Assert.Single(_asked);
        var entry = Assert.Single(audit!.References);
        Assert.Equal("saved answer reused", entry.Lookups[0].Status);
        Assert.Contains(entry.NotUsed, n => n.Contains("names another title"));
        Assert.Null(entry.Completed);
        Assert.Equal(before, GreyReferences.Line(GreyReferences.Build(ledger, pages, extraction)[3]));
    }

    [Fact]
    public async Task AWholeRunCitesTheCompletedFormalReferenceAndSaysHowInItsMethods()
    {
        var run = await FormalKit.RunAsync(Path.Join(_run, "runs"), referenceLookup: Answers(Crossref("Context summaries for LLM agents")));
        Assert.Null(run.Runner.StatusOf(run.RunId)?.Error);

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(run.Writer.Archive(run.RunId)));
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }

        // Only the formal source with a DOI is asked about; the grey sources and the one with an address alone are not.
        Assert.Single(_asked);
        var audit = System.Text.Json.JsonSerializer.Deserialize<ReferenceEnrichmentFile>(Read(ReferenceEnrichmentFile.FileName))!;
        Assert.NotNull(zip.GetEntry(Assert.Single(Assert.Single(audit.References).Lookups).RawFile!));
        string tex = Read(MultivocalPaper.MainTexFile);
        Assert.Contains(@"Doe, J., Lee, K., \& Park, A. (2025). Context summaries for LLM agents.\ \textit{Journal of Agents}, \textit{7}(2), 10–20.", tex);
        Assert.Contains("the reference of the one extracted academic source with a DOI or arXiv number was looked up in code at Crossref by that identifier, and it gained the full author list, the volume, the issue and the pages.", tex);
        Assert.Contains("volume = {7}", Read(GreyReferences.BibFile));
        // The ledger keeps the database's record.
        Assert.Equal(new[] { "Jane Doe", "Kim Lee" }, run.Searcher.LoadLedger(run.RunId)!.FormalPool.First(s => s.Paper!.Id == "OPENALEX_W1").Paper!.Authors);
    }
}

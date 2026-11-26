using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole offline systematic run with the reference enrichment on: Crossref's answer completes the reference in
/// main.tex, references.bib and references.ris, and the archive holds the answer and reference-enrichment.json.
/// </summary>
public class ReferenceEnrichmentRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "enrich-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Crossref = """
        {"status":"ok","message":{"DOI":"10.3390/fi1234567","type":"journal-article","title":["Agent Loops for Safe Orchestration"],
         "author":[{"given":"Jane","family":"Doe"},{"given":"Jun-wei","family":"Liu"}],
         "container-title":["Future Internet"],"volume":"12","issue":"3","page":"45-60","publisher":"MDPI AG"}}
        """;

    [Fact]
    public async Task CrossrefCompletesTheReferenceInEveryFileOfTheArchive()
    {
        var asked = new List<string>();
        var model = new FakeChatService().RespondsWith(ReviewWriterTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
            ReferenceLookup = url =>
            {
                asked.Add(url);
                return url.Contains("api.crossref.org/works/10.3390/fi1234567") ? Task.FromResult(Crossref)
                    : throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            },
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        string Read(string name) { using var r = new StreamReader(archive.GetEntry(name)!.Open()); return r.ReadToEnd(); }

        Assert.Single(asked, u => u.Contains("crossref"));
        Assert.Contains(@"} Doe, J., \& Liu, J.-W. (2025). Agent loops for safe orchestration.\ \textit{Future Internet}, \textit{12}(3), 45–60.\ \url{https://doi.org/10.3390/fi1234567}", Read("main.tex"));
        string bib = Read("references.bib");
        Assert.Contains("author = {Doe, Jane and Liu, Jun-wei}", bib);
        Assert.Contains("pages = {45--60}", bib);
        Assert.Contains("VL  - 12", Read("references.ris"));
        // The methods text says how the references were completed, in code from reference-enrichment.json.
        Assert.Contains("the reference of the one included study with a DOI or arXiv number was looked up in code at Crossref by that identifier, and it gained the full author list, the volume, the issue and the pages.\nNo language model was involved", Read("main.tex").Replace("\r\n", "\n")); // one sentence per line

        var audit = JsonSerializer.Deserialize<ReferenceEnrichmentFile>(Read(ReferenceEnrichmentFile.FileName))!;
        var entry = Assert.Single(audit.References, r => r.Doi == "10.3390/fi1234567");
        Assert.Equal(new[] { "authors", "issue", "pages", "volume" }, entry.Filled.Keys.Order());
        var lookup = Assert.Single(entry.Lookups);
        Assert.NotNull(archive.GetEntry(lookup.RawFile!));
        Assert.Contains(lookup.Sha256!, Read("manifest.json"));

        // The exports of this run, pinned byte for byte; set UPDATE_GOLDEN=1 to write them again after a deliberate change.
        Golden("systematic-references.bib", bib);
        Golden("systematic-references.ris", Read("references.ris"));
    }

    private static void Golden(string name, string actual, [CallerFilePath] string here = "")
    {
        string path = Path.Join(Path.GetDirectoryName(here)!, "Golden", name);
        actual = actual.Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path)) File.WriteAllText(path, actual);
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
    }
}

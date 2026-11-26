using System.IO.Compression;
using System.Net;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole offline systematic run with the retraction check on. One included study carries the DOI of Wakefield et al.
/// (1998), whose notices are Crossref's recorded answer: it is excluded before its full text is read, with the notice in
/// the ledger, a line in the flow diagram and a methods sentence. Another has an expression of concern (a short answer of
/// the same shape, written for this test): it stays in, marked in Table A.1.
/// </summary>
public class RetractionCheckRunTests : IDisposable
{
    private const string WakefieldDoi = "10.1016/S0140-6736(97)11096-0";
    private readonly string _root = Path.Join(Path.GetTempPath(), "retraction-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Concern = """
        {"status":"ok","message":{"items":[{"DOI":"10.3390/fi1234567.eoc","update-to":[{"DOI":"10.3390/fi1234567","type":"expression_of_concern",
         "label":"Expression of concern","source":"publisher","updated":{"date-parts":[[2026,1,2]]}}]}]}}
        """;

    [Fact]
    public async Task ARetractedStudyIsExcludedAndAConcernIsMarked()
    {
        var fetchedFullText = new List<string>();
        var model = new FakeChatService().RespondsWith(ReviewWriterTests.Respond);
        var answers = new Dictionary<string, string>
        {
            ["works?filter=updates:10.1016%2FS0140-6736%2897%2911096-0"] = RetractionCheckTests.Fixture("crossref-updates-wakefield-1998.json"),
            ["works/10.1016/S0140-6736"] = RetractionCheckTests.Work(WakefieldDoi, "Agent memory in early orchestration"),
            ["works?filter=updates:10.3390%2Ffi1234567"] = Concern,
            ["works/10.3390/fi1234567"] = RetractionCheckTests.Work("10.3390/fi1234567", "Agent loops for safe orchestration"),
        };
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(new AcademicPaper("SCHOLAR_w", "Agent memory in early orchestration",
                "Agent memory was studied.", "Published: 1998", new() { "Ann Field" }, "The Lancet", WakefieldDoi)),
            FullTextFetcher = (paper, _) =>
            {
                fetchedFullText.Add(paper.Id);
                return Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null));
            },
            ReferenceLookup = url => answers.FirstOrDefault(a => url.Contains(a.Key, StringComparison.Ordinal)).Value is { } body
                ? Task.FromResult(body)
                : throw new HttpRequestException("not found", null, HttpStatusCode.NotFound),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 5, SelectedSources: new[] { "arxiv" }));

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        var retracted = state.Phases.Screening.Single(l => l.PaperId == "SCHOLAR_w");
        Assert.Equal("Excluded", retracted.Decision);
        Assert.Equal(ExclusionReasons.Retracted, retracted.ExclusionReason);
        Assert.StartsWith("Excluded after screening: Crossref lists a retraction of this DOI (notice 10.1016/s0140-6736(10)60175-4, Retraction Watch, 2010-02-06). Screening had included it:", retracted.Reasoning);
        Assert.DoesNotContain("SCHOLAR_w", fetchedFullText);
        Assert.Equal(2, state.Stats.Included);
        var flow = PrismaFlowCounts.From(state);
        Assert.True(flow.IsConsistent);
        Assert.Equal(1, flow.ExcludedRetracted);
        Assert.DoesNotContain(state.SynthesizedRecords, r => r.PaperId == "SCHOLAR_w");
        Assert.Equal("Expression of concern on record (the publisher, 2026-01-02).",
            state.SynthesizedRecords.Single(r => r.Doi == "10.3390/fi1234567").IntegrityNote);

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        string Read(string name) { using var r = new StreamReader(archive.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        string tex = Read("main.tex").Replace(LatexBreaks.Break, "");
        Assert.Contains("Did not meet criteria (n = 1)", tex);
        Assert.Contains("Retracted (n = 1)", tex);
        Assert.Contains(@"\newline\textit{Expression of concern on record (the publisher, 2026-01-02).}", tex);
        Assert.Contains("the 2 included studies with a DOI registered at Crossref were checked in code for retractions", tex);
        Assert.Contains("Of them, 1 had been retracted and was excluded, with the notice as the reason in the screening ledger and 1 had an expression of concern; Table A.1 marks that study.", tex);
        var check = JsonSerializer.Deserialize<RetractionCheckFile>(Read(RetractionCheckFile.FileName))!;
        Assert.Equal(3, check.IncludedAtCheck);
        var lookup = check.Studies.Single(s => s.PaperId == "SCHOLAR_w").Lookups[1];
        Assert.NotNull(archive.GetEntry(lookup.RawFile!));
        Assert.Contains(lookup.Sha256!, Read("manifest.json"));
    }
}

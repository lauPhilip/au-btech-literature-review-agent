using System.IO.Compression;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The seed step of citation chaining in a whole offline systematic run: seeds are the included records with the most
/// citations per year, chosen in code; each is built out both ways with its neighbours ranked the same way; what they
/// find is screened, counted as "other methods" and reported in the methods text, the flow diagram and the appendix.
/// </summary>
public class SeedChainingTests : PipelineFeatureRun
{
    private static readonly AcademicPaper[] Extra =
    {
        new("SCHOLAR_d", "Agent memory hierarchies", "Agent memory.", "Published: 2016", new() { "Old Author" }, "Journal of Agents", "10.5555/old", CitedByCount: 500),
        new("SCHOLAR_e", "Agent tool use", "Agent tools.", "Published: 2025", new() { "New Author" }, "Journal of Agents", "10.5555/new", CitedByCount: 300),
        new("SCHOLAR_f", "Agent planning", "Agent planning.", "Published: 2020", new() { "Low Author" }, "Journal of Agents", "10.5555/low", CitedByCount: 7),
        new("SCHOLAR_g", "Wheat rust", "Agronomy.", "Published: 2025", new() { "Crop Author" }, "Field Crops Research", "10.5555/wheat", CitedByCount: 9000),
    };

    private sealed class SeedGraph : ICitationGraph
    {
        public List<string> PoolDois { get; } = new();
        public List<string> ChainedDois { get; } = new();
        public string SourceName => "OpenAlex";

        public Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection)
        {
            lock (ChainedDois) ChainedDois.Add(doi);
            return Task.FromResult(new CitationNeighbours(new(), new(), new[] { "{}" }));
        }

        public Task<CitationNeighbours> GetCitationPoolAsync(string doi, int poolPerDirection)
        {
            PoolDois.Add(doi);
            static AcademicPaper P(string id, string title, int year, int cites) =>
                new(id, title, "Abstract.", $"Published: {year}", new() { "A" }, "Venue", CitedByCount: cites);
            return Task.FromResult(doi == "10.5555/new"
                ? new CitationNeighbours(
                    new() { P("OPENALEX_N2", "Agent benchmarks", 2010, 17), P("OPENALEX_N1", "Agent evaluation", 2024, 30) },
                    new() { P("OPENALEX_N4", "Wheat yields", 2026, 1), P("OPENALEX_N3", "Agent survey", 2026, 40) },
                    new[] { "{\"seed\":\"new\"}" })
                : new CitationNeighbours(
                    new() { P("OPENALEX_N5", "Agent classics", 2000, 2700) },
                    new() { P("OPENALEX_N6", "Wheat irrigation", 2026, 3) },
                    new[] { "{\"seed\":\"old\"}" }));
        }
    }

    private PrismaReviewEngine SeedEngine(ICitationGraph graph) => new("test-key", "", null, null, null, new RunsOptions())
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
        SourceFactory = _ => new PipelineTests.FakeSource(Extra),
        CitationGraphFactory = () => graph,
        Cache = ReviewCache.Disabled,
        FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        OpenSources = new OpenSourcesOptions { SeedStudies = 2, SeedChainingPerDirection = 1 },
    };

    private static ReviewRequest SeedRequest() => new("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 10,
        SelectedSources: new[] { "arxiv" }, CitationChaining: true);

    [Fact]
    public async Task TheMostCitedIncludedStudiesPerYearAreBuiltOutAndTheirNeighboursScreened()
    {
        var graph = new SeedGraph();
        var engine = SeedEngine(graph);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, SeedRequest());
        var state = engine.LoadState(runId)!;

        // The excluded wheat paper has the most citations but is never a seed; the 2025 study beats the 2016 one per year.
        var seeds = state.SeedChaining!.Seeds;
        Assert.Equal(new[] { "SCHOLAR_e", "SCHOLAR_d" }, seeds.Select(s => s.PaperId));
        Assert.Equal(new[] { 1, 2 }, seeds.Select(s => s.Rank));
        Assert.Equal(new[] { 300, 500 }, seeds.Select(s => s.CitedByCount));
        Assert.Equal(1, state.SeedChaining.WithoutCount); // SCHOLAR_a has a DOI but no count
        Assert.Equal(new[] { "10.5555/new", "10.5555/old" }, graph.PoolDois);

        // The other included records with a DOI are chained as before; the seeds are not chained twice.
        Assert.Equal(new[] { "10.3390/fi1234567", "10.5555/low" }, graph.ChainedDois.OrderBy(d => d));

        // One neighbour per direction, the most cited per year: N1 over N2, N3 over N4.
        var fromSeeds = state.Phases.Screening.Where(l => l.Origin == PrismaReviewEngine.OriginSeedChaining).ToList();
        Assert.Equal(new[] { "OPENALEX_N1", "OPENALEX_N3", "OPENALEX_N5", "OPENALEX_N6" }, fromSeeds.Select(l => l.PaperId));
        Assert.Equal(new[] { "Included", "Included", "Included", "Excluded" }, fromSeeds.Select(l => l.Decision));
        Assert.Equal(30, fromSeeds[0].CitedByCount);
        Assert.Equal((1, 1), (seeds[0].Backward, seeds[0].Forward));

        Assert.Equal(4, state.Stats.IdentifiedViaSeedChaining);
        Assert.Equal(4, state.Stats.IdentifiedViaCitations);
        var counts = PrismaFlowCounts.From(state);
        Assert.True(counts.IsConsistent);
        Assert.DoesNotContain(counts.IdentifiedBySource, s => s.Source.Contains("chaining"));
        Assert.Contains("citation chaining from the most-cited included studies (n = 4)", PrismaFlowDiagram.ToTikz(counts));
        Assert.Contains(state.SearchLogs, l => l.SourceName == "OpenAlex (seed chaining)" && l.RawResponseFile != null && l.RawResponseSha256 != null);
        Assert.True(File.Exists(Path.Join(_root, runId.ToString("N"), PrismaReviewEngine.RawResponsesFolder, "seed-01.json")));

        string report = File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json"));
        Assert.Contains("were taken as seed studies", report);
        Assert.Contains("not by a language model", report);
        Assert.Contains("chosen in code from the citation counts", AiUseStatement.Methods(state, runId));
        Assert.Contains("Seed studies: the 2 included studies", File.ReadAllText(Path.Join(_root, runId.ToString("N"), "protocol.md")));

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string tex = reader.ReadToEnd();
        Assert.Matches(@"Table A\.\d: Seed studies for citation chaining", tex);
        Assert.Contains("Agent tool use & 2025 & 300 &", tex);
    }

    [Fact]
    public async Task WithoutCitationCountsNoSeedIsChosenAndTheMethodSaysWhy()
    {
        var graph = new SeedGraph();
        var engine = SeedEngine(graph);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request(chaining: true));
        var state = engine.LoadState(runId)!;

        Assert.Empty(graph.PoolDois);
        Assert.Empty(state.SeedChaining!.Seeds);
        Assert.Equal(0, state.Stats.IdentifiedViaSeedChaining);
        Assert.Contains("No seed studies were chosen", File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")));
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #447: the coverage sentence says what the synthesis and discussion cite, so a study cited only in the rationale
/// is not counted as synthesised.
/// </summary>
public class CoverageScopeTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "coverage-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string Respond(string prompt)
    {
        // The theme and its fill pass cite study 1 only; the discussion too; the rationale cites study 2.
        if (prompt.Contains("You are revising one theme subsection") || prompt.Contains("You are the author revising one section"))
            return """{"text":"Agent loops recover from faults through supervisor checks [1]."}""";
        if (prompt.Contains("You are writing the Discussion section"))
            return """{"text":"Recovery matters most [1]."}""";
        if (prompt.Contains("You are writing the title, abstract, rationale and objectives"))
            return """{"title":"Agent loops: a systematic review","abstract":"We screened the records and included 2 studies.","rationale":"Governance layers audit agent actions [2].","objectives":"To map agent loops."}""";
        return ReviewWriterTests.Respond(prompt);
    }

    [Fact]
    public async Task AStudyCitedOnlyInTheRationaleIsNotCountedAsSynthesised()
    {
        var model = new FakeChatService().RespondsWith(Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.Contains("[2]", report.RationaleItem);
        Assert.Contains("cite 1 of the 2 included studies", report.CoverageSummary);
        Assert.Equal(1, engine.LoadState(runId)!.Stats.StudiesCitedInSynthesis);
    }
}

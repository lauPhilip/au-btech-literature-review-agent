using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Runs the whole review pipeline offline: a fake source returns fixed papers and a fake model answers each
/// prompt type with canned JSON. Checks that the stages, the human review pause, the ledger, the citation
/// check and the archive all fit together.
/// </summary>
public class PipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private class FakeSource : IAcademicSource
    {
        public string SourceName => "Google Scholar Gateway";
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5) => Task.FromResult(new List<AcademicPaper>
        {
            new("SCHOLAR_a", "Agent loops for safe orchestration", "Agent loops recover from faults through supervisor checks.", "Published: 2025", new() { "Jane Doe" }, "Future Internet", "10.3390/fi1234567"),
            new("SCHOLAR_b", "Crop yields in wheat", "An agronomy study of wheat yields.", "Published: 2024", new() { "Ola Nordmann" }, "Field Crops Research"),
            new("SCHOLAR_c", "Agent governance layers", "Governance layers audit agent actions in production.", "Published: 2026", new() { "Kim Lee" }, "Proceedings of the Conference on Agents"),
        }); // returns all three whatever the cap, like an adapter that over-returns; the engine enforces the cap
    }

    private static string Respond(string prompt)
    {
        if (prompt.Contains("Propose exactly 3 additional")) return """{"perspectives":["agent orchestration safety","agent fault recovery"]}""";
        if (prompt.Contains("Evaluate the following academic paper"))
        {
            bool agent = Regex.IsMatch(prompt, @"- Title: Agent");
            return agent
                ? """{"decision":"Included","reasoning":"About agent architecture.","briefSummary":"An agent paper."}"""
                : """{"decision":"Excluded","reasoning":"Agronomy.","briefSummary":"A crop paper."}""";
        }
        if (prompt.Contains("grounded synthesis outline")) return """{"themes":[{"theme":"Loops","claims":[{"text":"Loops recover from faults","refs":[1]}]}]}""";
        if (prompt.Contains("elite academic meta-analyst"))
            return """{"titleItem":"Agent Loops: A Systematic Review","abstractItem":"We review 3 papers on agent loops.","rationaleItem":"Agents need safety.","objectivesItem":"To map agent loop safety.","biasAssessmentItem":"x","synthesisResultsItem":"Draft [1].","discussionItem":"Draft [1].","supportItem":"x","availabilityItem":"x"}""";
        if (prompt.Contains("rigorous academic copyeditor"))
        {
            var m = Regex.Match(prompt, @"ORIGINAL DRAFT:\s*(.*?)\s*TASK:", RegexOptions.Singleline);
            return m.Groups[1].Value.Trim();
        }
        if (prompt.Contains("Results & Synthesis section (Item 20a)"))
            return """{"synthesis":"Agent loops recover from faults through supervisor checks [1]. Governance layers audit agent actions [2].","discussion":"Loops and audits complement each other [1, 2]."}""";
        if (prompt.Contains("strict but constructive peer reviewer")) return """{"comments":[]}""";
        if (prompt.Contains("You are checking citations"))
        {
            // Quote the first 40 characters of excerpt E1 for every sentence.
            string e1 = Regex.Match(prompt, @"^E1: (.*)$", RegexOptions.Multiline).Groups[1].Value;
            int n = Regex.Matches(prompt, @"^S\d+: ", RegexOptions.Multiline).Count;
            var items = Enumerable.Range(1, n).Select(i => new { sentence = i, verdict = "supported", excerpt = "E1", quote = e1[..Math.Min(40, e1.Length)], reason = "ok" });
            return JsonSerializer.Serialize(new { results = items });
        }
        return "{}";
    }

    private PrismaReviewEngine Engine(int maxConcurrent = 3) => new("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = maxConcurrent })
    {
        WorkspaceRoot = _root,
        ChatFactory = _ => new FakeChatService().RespondsWith(Respond),
        SourceFactory = _ => new FakeSource(),
    };

    private static ReviewRequest Request(bool humanReview = false) => new(
        "agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
        SelectedSources: new[] { "arxiv" }, HumanScreeningReview: humanReview);

    [Fact]
    public async Task FullRunProducesAConsistentLedgerCitationChecksAndArchive()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();

        await engine.RunReviewAsync(runId, Request());

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        Assert.False(engine.IsRunActive(runId));
        Assert.Equal(2, state.Stats.Included);
        Assert.Equal(1, state.Stats.Excluded);
        Assert.True(PrismaFlowCounts.From(state).IsConsistent);
        Assert.Equal(new[] { 1, 2 }, state.SynthesizedRecords.Select(r => r.ReferenceNumber));
        Assert.Equal(4, state.Stats.CitationsChecked); // [1], [2], and [1, 2] counts twice
        Assert.Equal(4, state.Stats.CitationsSupported);

        Assert.NotNull(state.RunSettings);
        Assert.True(state.RunSettings!.LlmCalls > 5);
        Assert.Equal(0.0, state.RunSettings.StageTemperatures["screening"]);
        Assert.True(File.Exists(Path.Combine(_root, runId.ToString("N"), "llm-calls.json")));

        byte[] zip = engine.GenerateWorkspaceArchiveFromDisk(runId);
        using var archive = new ZipArchive(new MemoryStream(zip));
        var names = archive.Entries.Select(e => e.FullName).ToList();
        foreach (var expected in new[] { "main.tex", "references.bib", "references.ris", "citation-audit.json", "llm-calls.json", "transparent-process.json", "prisma-report.json" })
            Assert.Contains(expected, names);

        string tex = new StreamReader(archive.GetEntry("main.tex")!.Open()).ReadToEnd();
        Assert.Contains("PRISMA 2020 flow diagram", tex);
        Assert.Contains("Automated Citation Check", tex);

        string audit = new StreamReader(archive.GetEntry("citation-audit.json")!.Open()).ReadToEnd();
        Assert.Contains("\"SupportChecks\"", audit);
    }

    [Fact]
    public async Task RunPausesForTheHumanReviewAndAppliesIt()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        var run = engine.RunReviewAsync(runId, Request(humanReview: true));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!engine.IsAwaitingScreeningReview(runId) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(engine.IsAwaitingScreeningReview(runId));
        Assert.Equal(PrismaReviewEngine.StageAwaitingReview, engine.LoadState(runId)!.Stats.ProcessingStage);

        // Reviewer excludes one of the model's two inclusions.
        Assert.True(engine.SubmitScreeningReview(runId, new[]
        {
            new ScreeningOverride("SCHOLAR_a", "Included", null),
            new ScreeningOverride("SCHOLAR_c", "Excluded", "Not about loops"),
        }));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        Assert.Equal(1, state.Stats.Included);
        Assert.Equal(2, state.Stats.HumanReviewed);
        Assert.Equal(1, state.Stats.HumanOverrides);
        Assert.Single(state.SynthesizedRecords);
        Assert.Contains("changed 1", state.HumanScreeningReviewOutcome);
        Assert.True(PrismaFlowCounts.From(state).IsConsistent);

        var report = File.ReadAllText(Path.Combine(_root, runId.ToString("N"), "prisma-report.json"));
        Assert.Contains("A human reviewer then checked 2", report);
    }

    [Fact]
    public async Task RunsBeyondTheLimitWaitAndStillFinish()
    {
        var engine = Engine(maxConcurrent: 1);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => engine.RunReviewAsync(id, Request())));

        foreach (var id in ids)
            Assert.Equal(PrismaReviewEngine.StageComplete, engine.LoadState(id)!.Stats.ProcessingStage);
    }

    [Fact]
    public void UnfinishedRunsAreMarkedInterruptedAtStartup()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_root, runId.ToString("N")));
        var state = new ReviewState();
        state.Stats.ProcessingStage = PrismaReviewEngine.StageScreening;
        File.WriteAllText(Path.Combine(_root, runId.ToString("N"), "transparent-process.json"), JsonSerializer.Serialize(state));

        Assert.Equal(1, engine.MarkInterruptedRuns());
        Assert.Equal(PrismaReviewEngine.StageInterrupted, engine.LoadState(runId)!.Stats.ProcessingStage);
        Assert.Equal(0, engine.MarkInterruptedRuns());
    }
}

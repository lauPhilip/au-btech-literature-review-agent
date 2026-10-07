using System.Text.Json.Nodes;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Search strings approved before the run, the saturation hint, exclusion reasons, the risk-of-bias overview and
/// re-checking one citation after the run.
/// </summary>
public class MethodQualityTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "method-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private PrismaReviewEngine Engine(Func<string, string>? respond = null, FakeChatService? chat = null) =>
        new("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => chat ?? new FakeChatService().RespondsWith(respond ?? PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };

    private static ReviewRequest Request(IReadOnlyList<string>? searchStrings = null, int maxResults = 3) => new(
        "agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", maxResults,
        SelectedSources: new[] { "arxiv" }, DualScreening: false, SearchStrings: searchStrings);

    private static AcademicPaper Paper(string id, string title, string? doi = null) =>
        new(id, title, "abstract", "2025", new() { "A. Author" }, "Journal", doi);

    // ── Saturation ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachSearchStringIsCreditedOnlyWithRecordsNoEarlierStringFound()
    {
        var p1 = Paper("a", "Agent loops");
        var p2 = Paper("b", "Agent audits", "10.1234/abc");
        var p2Elsewhere = Paper("other-id", "A different title", "10.1234/ABC"); // same DOI from another source
        var p3 = Paper("c", "Agent memory");
        var yields = SearchSaturation.Compute(new[] { "q1", "q2", "q3" }, new (int, IReadOnlyList<AcademicPaper>)[]
        {
            (0, new[] { p1, p2 }), (0, new[] { p2Elsewhere }),
            (1, new[] { p2, p3 }),
            (2, new[] { p1 }),
        });

        Assert.Equal(new[] { 2, 1, 1 }, yields.Select(y => y.Searches));
        Assert.Equal(new[] { 3, 2, 1 }, yields.Select(y => y.Retrieved));
        Assert.Equal(new[] { 2, 1, 0 }, yields.Select(y => y.New));
        Assert.Contains("close to saturation", SearchSaturation.Hint(yields));
    }

    [Fact]
    public void ALastStringThatStillAddsALotMeansTheSearchHasNotSettled()
    {
        var yields = new[] { new SearchStringYield("q1", 1, 10, 10), new SearchStringYield("q2", 1, 10, 6) };
        string hint = SearchSaturation.Hint(yields)!;
        Assert.StartsWith("The last search string added 6 new records (of 10 retrieved); the earlier ones added 10.", hint);
        Assert.Contains("has not settled", hint);
    }

    [Fact]
    public void StringsThatWereNotRunAreNamedAndLeftOutOfTheHint()
    {
        var yields = new[] { new SearchStringYield("q1", 1, 8, 8), new SearchStringYield("q2", 1, 8, 0), new SearchStringYield("q3", 0, 0, 0) };
        string hint = SearchSaturation.Hint(yields)!;
        Assert.Contains("added 0 new records", hint);
        Assert.Contains("1 further string(s) were not needed", hint);
        Assert.Null(SearchSaturation.Hint(new[] { new SearchStringYield("q1", 1, 8, 8) }));
    }

    // ── Search strings approved before the run ───────────────────────────────────────────────────

    [Fact]
    public void ApprovedSearchStringsAreCleanedAndChecked()
    {
        var ok = ReviewInputGuard.Check(Request(new[] { "  agent  safety ", "AGENT LOOPS", "", "agent safety", "agent memory" }));
        Assert.True(ok.IsValid);
        Assert.Equal(new[] { "agent safety", "agent memory" }, ok.Request.SearchStrings);

        var tooMany = ReviewInputGuard.Check(Request(new[] { "a1", "a2", "a3", "a4", "a5" }));
        Assert.Contains(tooMany.Errors, e => e.Contains("limit is 4"));

        var tooLong = ReviewInputGuard.Check(Request(new[] { new string('x', ReviewInputGuard.MaxSearchString + 1) }));
        Assert.Contains(tooLong.Errors, e => e.StartsWith("Search string 2 is"));

        Assert.Null(ReviewInputGuard.Check(Request()).Request.SearchStrings);
    }

    [Fact]
    public async Task ApprovedSearchStringsAreUsedAsTheyAreAndRecorded()
    {
        var chat = new FakeChatService().RespondsWith(PipelineTests.Respond);
        var engine = Engine(chat: chat);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request(new[] { "agent safety" }, maxResults: 10));

        Assert.DoesNotContain(chat.Prompts, p => p.Contains("Propose exactly 3 additional"));
        var state = engine.LoadState(runId)!;
        Assert.Equal(new[] { "agent loops", "agent safety" }, state.SearchPerspectives);
        Assert.True(state.SearchStringsReviewed);
        string protocol = File.ReadAllText(Path.Join(_root, runId.ToString("N"), "protocol.md"));
        Assert.Contains("saw the extra search strings before the run and approved or edited them", protocol);

        // The fake source returns the same three records for every query, so the second string adds nothing new.
        Assert.Equal(new[] { 3, 0 }, state.SearchStringYields.Select(y => y.New));
    }

    [Fact]
    public async Task APreviewReturnsOnlyTheExtraStrings()
    {
        var engine = Engine();
        var strings = await engine.PreviewSearchStringsAsync("agent loops", "Map agent loop safety", "Agent architecture", null);
        Assert.Equal(new[] { "agent orchestration safety", "agent fault recovery" }, strings);
    }

    [Fact]
    public void TheMethodsTextSaysTheReviewerApprovedTheStringsAndHowMuchEachAdded()
    {
        var yields = new[] { new SearchStringYield("agent loops", 1, 10, 10), new SearchStringYield("agent safety", 1, 10, 0) };
        string text = MethodsSectionWriter.SearchStrategy("agent loops", new[] { "agent loops", "agent safety" }, 10, 0, 0,
            reviewedByReviewer: true, yields: yields);
        Assert.Contains("checked and approved (or edited) by the reviewer before the search", text);
        Assert.Contains("the search strings added 10, 0 records not found by an earlier string, so the search was close to saturation", text);
    }

    // ── Exclusion reasons ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnExclusionReasonIsOptionalButMustBeAKnownGroup()
    {
        Assert.Null(ScreeningAnswer.Validate(new ScreeningAnswer { Decision = "Excluded", Reasoning = "r" }));
        Assert.Null(ScreeningAnswer.Validate(new ScreeningAnswer { Decision = "Excluded", Reasoning = "r", ExclusionReason = "Off-Topic" }));
        Assert.Contains("exclusionReason must be one of", ScreeningAnswer.Validate(new ScreeningAnswer { Decision = "Excluded", Reasoning = "r", ExclusionReason = "boring" }));
        Assert.Null(new ScreeningAnswer { Decision = "Included", Reasoning = "r", ExclusionReason = "off-topic" }.Normalized().ExclusionReason);
        Assert.Equal("off-topic", new ScreeningAnswer { Decision = "Excluded", Reasoning = "r", ExclusionReason = " Off-Topic " }.Normalized().ExclusionReason);
    }

    [Fact]
    public async Task ExclusionsAreGroupedByReasonInTheLedgerFlowDiagramAndReport()
    {
        static string Respond(string prompt) =>
            prompt.Contains("Evaluate the following academic paper") && prompt.Contains("- Title: Crop")
                ? """{"decision":"Excluded","reasoning":"Agronomy.","briefSummary":"A crop paper.","exclusionReason":"off-topic"}"""
                : PipelineTests.Respond(prompt);
        var engine = Engine(Respond);
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request());

        var state = engine.LoadState(runId)!;
        Assert.Equal("off-topic", state.Phases.Screening.Single(l => l.Title.StartsWith("Crop")).ExclusionReason);
        Assert.Null(state.Phases.Screening.First(l => l.Decision == "Included").ExclusionReason);
        Assert.Equal(new[] { ("off-topic", 1) }, ExclusionReasons.Count(state.Phases.Screening).Select(g => (g.Key, g.Count)));
        Assert.Contains(@"\quad Off topic (n = 1)", PrismaFlowDiagram.ToTikz(PrismaFlowCounts.From(state)));

        string report = File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json"));
        Assert.Contains("Reasons for exclusion at screening (1 record): off topic (1).", report);
    }

    [Fact]
    public void OldRunsWithoutReasonsGetNoReasonSentence()
    {
        var logs = new[] { new ScreeningLog("a", "t", "Excluded", "r", "c", "s") };
        Assert.Null(ExclusionReasons.Sentence(logs));
        Assert.Equal(ExclusionReasons.Unspecified, ExclusionReasons.Count(logs).Single().Key);
    }

    // ── Risk of bias ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRiskOfBiasOverviewCountsAnswersPerQuestionAndCategory()
    {
        StudyExtraction Study(int n, string category, params string[] answers) => new()
        {
            ReferenceNumber = n, AppraisalCategory = category,
            Appraisal = answers.Select((a, i) => new AppraisalAnswer { Id = $"4.{i + 1}", Criterion = $"Question {i + 1}", Answer = a }).ToList(),
        };
        var summary = RiskOfBias.Summarise(new[]
        {
            Study(1, "quantitative_descriptive", "yes", "no"),
            Study(2, "quantitative_descriptive", "yes", "cant_tell"),
            Study(3, "not_empirical"),
        });

        var category = Assert.Single(summary);
        Assert.Equal(2, category.Studies);
        Assert.Equal((2, 0, 0), (category.Questions[0].Yes, category.Questions[0].No, category.Questions[0].CantTell));
        Assert.Equal((0, 1, 1), (category.Questions[1].Yes, category.Questions[1].No, category.Questions[1].CantTell));
        Assert.Equal("Can't tell", RiskOfBias.For("cant_tell").Word);
        Assert.Equal("–", RiskOfBias.For(null).Word);
    }

    // ── Re-checking one citation ─────────────────────────────────────────────────────────────────

    /// <summary>Runs a review and marks one citation in the audit as not supported, as a real check might have.</summary>
    private async Task<(PrismaReviewEngine Engine, Guid RunId, CitationSupportResult Marked)> RunWithOneRedCitation()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request());
        string auditPath = Path.Join(_root, runId.ToString("N"), "citation-audit.json");
        var audit = JsonNode.Parse(File.ReadAllText(auditPath))!.AsObject();
        var first = audit["SupportChecks"]!.AsArray()[0]!.AsObject();
        first["Verdict"] = CitationSupportChecker.NotSupported;
        File.WriteAllText(auditPath, audit.ToJsonString());
        var marked = System.Text.Json.JsonSerializer.Deserialize<CitationSupportResult>(first.ToJsonString())!;
        return (engine, runId, marked);
    }

    [Fact]
    public async Task ARedCitationCanBeCheckedAgainAndTheChangeIsKept()
    {
        var (engine, runId, marked) = await RunWithOneRedCitation();
        Assert.Equal(PrismaReviewEngine.MaxRechecksPerRun, engine.RechecksLeft(runId));

        var result = await engine.RecheckCitationAsync(runId, marked.Field, marked.SentenceIndex, marked.Reference);

        Assert.Equal(CitationSupportChecker.Supported, result.Verdict);
        var audit = JsonNode.Parse(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "citation-audit.json")))!.AsObject();
        var recheck = Assert.Single(audit["Rechecks"]!.AsArray())!;
        Assert.Equal(CitationSupportChecker.NotSupported, recheck["VerdictBefore"]!.GetValue<string>());
        Assert.Equal(CitationSupportChecker.Supported, recheck["VerdictAfter"]!.GetValue<string>());
        Assert.Equal(4, audit["SupportSummary"]!["Supported"]!.GetValue<int>());
        Assert.Equal(4, engine.LoadState(runId)!.Stats.CitationsSupported);
        Assert.Equal(PrismaReviewEngine.MaxRechecksPerRun - 1, engine.RechecksLeft(runId));

        string report = File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json"));
        Assert.Contains("1 citation was checked again on request after the run", report);
    }

    [Fact]
    public async Task OnlyOrangeAndRedCitationsCanBeCheckedAgain()
    {
        var (engine, runId, _) = await RunWithOneRedCitation();
        var state = engine.LoadState(runId)!;
        string auditPath = Path.Join(_root, runId.ToString("N"), "citation-audit.json");
        var green = System.Text.Json.JsonSerializer.Deserialize<List<CitationSupportResult>>(
            JsonNode.Parse(File.ReadAllText(auditPath))!["SupportChecks"]!.ToJsonString())!
            .First(c => c.Verdict == CitationSupportChecker.Supported);

        var ex = await Assert.ThrowsAsync<PrismaReviewEngine.RecheckException>(() =>
            engine.RecheckCitationAsync(runId, green.Field, green.SentenceIndex, green.Reference));
        Assert.Contains("Only partly supported and not supported", ex.Message);
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
    }

    [Fact]
    public async Task ARunGetsALimitedNumberOfRechecks()
    {
        var (engine, runId, marked) = await RunWithOneRedCitation();
        string auditPath = Path.Join(_root, runId.ToString("N"), "citation-audit.json");
        var audit = JsonNode.Parse(File.ReadAllText(auditPath))!.AsObject();
        var used = new JsonArray();
        for (int i = 0; i < PrismaReviewEngine.MaxRechecksPerRun; i++) used.Add(new JsonObject { ["Field"] = "x" });
        audit["Rechecks"] = used;
        File.WriteAllText(auditPath, audit.ToJsonString());

        Assert.Equal(0, engine.RechecksLeft(runId));
        var ex = await Assert.ThrowsAsync<PrismaReviewEngine.RecheckException>(() =>
            engine.RecheckCitationAsync(runId, marked.Field, marked.SentenceIndex, marked.Reference));
        Assert.Contains("used its", ex.Message);
    }
}

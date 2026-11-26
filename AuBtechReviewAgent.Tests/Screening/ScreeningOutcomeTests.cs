using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Pins down the screening stage as it is: the exact cache keys of a search answer and of both screenings, how
/// two screenings are combined into one ledger line, the peer-review filter, a failed screening call, and the
/// parts of the screening prompt.
/// </summary>
public class ScreeningOutcomeTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "screening-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Inclusion = "Agent architecture";
    private const string Exclusion = "Agronomy";

    private PrismaReviewEngine Engine(Func<string, string> respond, IAcademicSource? source = null, ReviewCache? cache = null) =>
        new("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = Path.Join(_root, "runs"),
            ChatFactory = _ => new FakeChatService().RespondsWith(respond),
            SourceFactory = _ => source ?? new PipelineTests.FakeSource(),
            Cache = cache ?? ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };

    private static ReviewRequest Request(bool peerReviewOnly = false) => new("agent loops", "Map agent loop safety", Inclusion, Exclusion, 3,
        PeerReviewOnly: peerReviewOnly, SelectedSources: new[] { "arxiv" }, SearchStrings: Array.Empty<string>());

    private static string Title(string prompt) => Regex.Match(prompt, @"- Title: (.*)").Groups[1].Value.Trim();
    private static bool Second(string prompt) => prompt.Contains("SECOND, INDEPENDENT screener");

    private static async Task<ReviewState> Run(PrismaReviewEngine engine, ReviewRequest request)
    {
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, request);
        return engine.LoadState(runId)!;
    }

    [Fact]
    public async Task CachedAnswersAreFoundUnderTheExactKeys()
    {
        var cache = new ReviewCache(new CacheOptions { Folder = Path.Join(_root, "cache") }, _root);
        var paper = new AcademicPaper("CACHED_1", "Agent cached paper", "An abstract about agents.", "Published: 2025", new() { "Jane Doe", "Kim Lee" }, "Future Internet");
        cache.Set("search", ReviewCache.Key("search-v1", "Google Scholar Gateway", "agent loops", "3"),
            new { Papers = new List<AcademicPaper> { paper }, Raw = new List<string> { "{\"cached\":true}" } });
        string ScreeningKey(string screener, string model) => ReviewCache.Key(PrismaReviewEngine.ScreeningPromptVersion, PrismaReviewEngine.ScreeningTemplateSha256(screener == "second"), screener, model, Inclusion, Exclusion,
            paper.Title, paper.Abstract, paper.JournalSource, paper.PublishedDate, "Jane Doe;Kim Lee");
        cache.Set("screening", ScreeningKey("first", "mistral-large-latest"),
            new ScreeningAnswer { Decision = "Included", Reasoning = "cached first", BriefSummary = "cached", Confidence = "high" });
        cache.Set("screening", ScreeningKey("second", "mistral-large-latest"),
            new ScreeningAnswer { Decision = "Included", Reasoning = "cached second", BriefSummary = "cached 2", Confidence = "medium" });

        var source = new PipelineTests.FakeSource();
        var state = await Run(Engine(PipelineTests.Respond, source, cache), Request());

        Assert.Equal(0, source.Calls);
        Assert.True(state.SearchLogs.Single().FromCache);
        var log = state.Phases.Screening.Single();
        Assert.Equal(("CACHED_1", "Included", "cached first", "cached second", "cached", "medium"), (log.PaperId, log.Decision, log.Reasoning, log.SecondReasoning, log.BriefSummary, log.Confidence));
        Assert.True(log.FromCache);
        Assert.Equal(3, state.Stats.CacheHits); // the search answer and both screenings
    }

    [Fact]
    public async Task TwoScreeningsAreCombinedIntoOneLedgerLine()
    {
        string Respond(string prompt)
        {
            if (!prompt.Contains("Evaluate the following academic paper")) return PipelineTests.Respond(prompt);
            bool second = Second(prompt);
            return Title(prompt) switch
            {
                "Agent loops for safe orchestration" => $$"""{"decision":"Included","reasoning":"R{{(second ? 2 : 1)}}","briefSummary":"S","confidence":"{{(second ? "medium" : "high")}}"}""",
                "Crop yields in wheat" => """{"decision":"Excluded","reasoning":"Crops.","briefSummary":"C","confidence":"high"}""",
                _ => second ? """{"decision":"Excluded","reasoning":"No.","briefSummary":"G2","confidence":"high","exclusionReason":"off-topic"}"""
                            : """{"decision":"Included","reasoning":"Yes.","briefSummary":"G","confidence":"high"}""",
            };
        }

        var state = await Run(Engine(Respond), Request());
        var logs = state.Phases.Screening.ToDictionary(l => l.PaperId);

        var loops = logs["SCHOLAR_a"];
        Assert.Equal(("Included", "R1", "Included", "R2", "medium", false), (loops.Decision, loops.Reasoning, loops.SecondDecision, loops.SecondReasoning, loops.Confidence, loops.Uncertain));
        Assert.Null(loops.ExclusionReason);

        var crops = logs["SCHOLAR_b"];
        Assert.Equal(("Excluded", ExclusionReasons.Unspecified, "C"), (crops.Decision, crops.ExclusionReason, crops.BriefSummary));

        var governance = logs["SCHOLAR_c"];
        Assert.Equal("Included", governance.Decision);
        Assert.Equal("The two independent screenings disagreed (first: Included, second: Excluded), so the record was kept and flagged for human attention. First screening: Yes.", governance.Reasoning);
        Assert.True(governance.Uncertain);
        Assert.Null(governance.ExclusionReason);

        Assert.Equal((3, 2, 1, 3, 1, 1), (state.Stats.Screened, state.Stats.Included, state.Stats.Excluded, state.Stats.DualScreened, state.Stats.ScreeningDisagreements, state.Stats.UncertainDecisions));
        Assert.All(state.Phases.Screening, l => Assert.Equal(PrismaReviewEngine.OriginDatabase, l.Origin));
    }

    [Fact]
    public async Task TheFilterExcludesRecordsThatAreNotPeerReviewed()
    {
        string Respond(string prompt) => prompt.Contains("has undergone formal peer review")
            ? (prompt.Contains("Crop yields") ? """{"isPeerReviewed":false}""" : """{"isPeerReviewed":true}""")
            : PipelineTests.Respond(prompt);

        var state = await Run(Engine(Respond), Request(peerReviewOnly: true));
        var crop = state.Phases.Screening.Single(l => l.PaperId == "SCHOLAR_b");

        Assert.Equal("Excluded", crop.Decision);
        Assert.StartsWith("Excluded during post-retrieval pre-screening: Document was classified as an un-reviewed preprint", crop.Reasoning);
        Assert.Equal(("N/A", ExclusionReasons.NotPeerReviewed), (crop.BriefSummary, crop.ExclusionReason));
        Assert.Null(crop.SecondDecision);
        Assert.Equal((1, 2, 3), (state.Stats.FailedPeerReviewCheck, state.Stats.PassedPeerReviewCheck, state.Stats.Screened));
    }

    [Fact]
    public async Task AFailedScreeningIsLoggedAsNotScreened()
    {
        string Respond(string prompt) => prompt.Contains("Evaluate the following academic paper") && Title(prompt) == "Crop yields in wheat"
            ? "not json at all" : PipelineTests.Respond(prompt);

        var state = await Run(Engine(Respond), Request());
        var crop = state.Phases.Screening.Single(l => l.PaperId == "SCHOLAR_b");

        Assert.Equal(("Error", "N/A"), (crop.Decision, crop.BriefSummary));
        Assert.StartsWith("Not screened: the model call failed (", crop.Reasoning);
        Assert.Equal((1, 2, 2), (state.Stats.ScreeningErrors, state.Stats.Screened, state.Stats.Included));
    }

    [Fact]
    public async Task ThePromptHoldsTheRecordTheStepsAndTheAnswerFormat()
    {
        var prompts = new List<string>();
        var chat = new FakeChatService().RespondsWith(p => { prompts.Add(p); return """{"decision":"Included","reasoning":"r","briefSummary":"s"}"""; });
        var paper = new AcademicPaper("X", "Agent loops", "", "Published: 2025", new() { "Jane Doe", "Kim Lee" }, "Future Internet");

        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, paper, "inc", "exc");
        await PrismaReviewEngine.ScreenPaperAsync(chat, paper with { Abstract = "About agents." }, "inc", "exc", secondScreener: true);

        Assert.Equal(("Included", "r", "s"), (answer.Decision, answer.Reasoning, answer.BriefSummary));
        string first = prompts[0], second = prompts[1];
        Assert.StartsWith("Evaluate the following academic paper against the provided systematically structured PRISMA parameters.", first);
        Assert.Contains("- Inclusion Thresholds: inc", first);
        Assert.Contains("- Exclusion Thresholds: exc", first);
        Assert.Contains("- Authors: Jane Doe, Kim Lee", first);
        Assert.Contains("- Abstract: (none: this record has no abstract)", first);
        Assert.Contains("THIS RECORD HAS NO ABSTRACT. Judge it on the title and venue only", first);
        Assert.Contains(PrismaReviewEngine.ScreeningProcedure(false).Trim(), first);
        Assert.Contains(PrismaReviewEngine.ScreeningAnswerFormat.Trim(), first);
        Assert.Contains("(The reference string is built separately from the source metadata - do not write one.)", first);

        Assert.Contains("- Abstract: About agents.", second);
        Assert.DoesNotContain("THIS RECORD HAS NO ABSTRACT", second);
        Assert.Contains(PrismaReviewEngine.ScreeningProcedure(true).Trim(), second);
    }
}

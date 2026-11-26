using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Screening against each part of the inclusion criterion (issue #564), with the records and the criterion of real
/// run 289a0d3e, where a design paper was let in because its abstract "suggests" an evaluation.
/// </summary>
public class CriterionPartsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "criterion-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class OneRecordSource : IAcademicSource
    {
        public string SourceName => "OpenAlex";
        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5) => Task.FromResult(new List<AcademicPaper> { DesignPaper });
    }

    private const string Inclusion = "Studies that describe or evaluate an agent framework or orchestration loop, with some form of evaluation.";
    private const string Exclusion = "Studies about agriculture or marketing; opinion pieces without evidence.";

    private static readonly AcademicPaper DesignPaper = new("OPENALEX_W7140235652", "Design of Multi Agent Autonomous Workflow Systems using Agentic AI Frameworks",
        "The objective of this paper is to examine the construction and design of multi-agent autonomous workflow systems (MAWS) that utilize agentic AI systems, as well as their impact on the evolution of business systems and the efficiency of business processes and decision-making. The paper concentrates on the architectural design, implementation strategies, and applications of systems cantered on MAS, emphasizing decision intelligence, scalability, and flexibility as paramount.",
        "Published: 2025", new() { "A. Author" }, "Journal");

    private static readonly AcademicPaper EvaluatedPaper = new("S2_ba278d4a65b58b4086980beb8add1b64e83bb82f", "Large Language Model Based Multi-Agent System Augmented Complex Event Processing Pipeline for Internet of Multimedia Things",
        "This paper presents the development and evaluation of a Large Language Model (LLM), also known as foundation models, based multi-agent system framework for complex event processing (CEP) with a focus on video query processing use cases. Extensive experiments evaluate the system's performance across varying configurations, complexities, and video resolutions, revealing the trade-offs between functionality and latency.",
        "Published: 2024", new() { "B. Author" }, "Journal");

    private static string Answer(string evaluationQuote) => $$"""
        {"decision":"Included","reasoning":"The paper describes an agent framework.","briefSummary":"s","confidence":"high",
         "criteriaParts":[{"part":"describe or evaluate an agent framework or orchestration loop","met":"yes","quote":"examine the construction and design of multi-agent autonomous workflow systems"},
                          {"part":"with some form of evaluation","met":"yes","quote":"{{evaluationQuote}}"}]}
        """;

    [Fact]
    public async Task ADesignPaperWhoseEvaluationIsOnlySuggestedIsExcludedInCode()
    {
        // The real rationale: "The abstract mentions architectural design, implementation strategies, and applications, suggesting a focus on system evaluation."
        var chat = new FakeChatService().RespondsWith(_ => Answer("suggesting a focus on system evaluation"));
        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, DesignPaper, Inclusion, Exclusion);

        Assert.Equal("Excluded", answer.Decision);
        Assert.Equal(ExclusionReasons.TooLittleInformation, answer.ExclusionReason);
        Assert.StartsWith("(Excluded in code: the record does not show this part of the inclusion criterion with a quote from its title or abstract: \"with some form of evaluation\".", answer.Reasoning);
        var parts = answer.CriteriaParts!;
        Assert.Equal(CriterionParts.Yes, parts[0].Met);
        Assert.Null(parts[0].Note);
        Assert.Equal(CriterionParts.Unclear, parts[1].Met);
        Assert.Equal("Marked unclear in code: the quote was not found in the title or abstract.", parts[1].Note);
    }

    [Fact]
    public async Task APaperThatShowsEveryPartWithAQuoteStaysIncluded()
    {
        string answerJson = Answer("Extensive experiments evaluate the system's performance").Replace(
            "examine the construction and design of multi-agent autonomous workflow systems", "based multi-agent system framework for complex event processing");
        var chat = new FakeChatService().RespondsWith(_ => answerJson);
        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, EvaluatedPaper, Inclusion, Exclusion);

        Assert.Equal("Included", answer.Decision);
        Assert.All(answer.CriteriaParts!, p => Assert.Equal(CriterionParts.Yes, p.Met));
    }

    [Fact]
    public async Task ThePromptAsksForEachPartAndAnAnswerWithoutPartsStaysValid()
    {
        var prompts = new List<string>();
        var chat = new FakeChatService().RespondsWith(p => { prompts.Add(p); return """{"decision":"Included","reasoning":"r","briefSummary":"s"}"""; });
        var answer = await PrismaReviewEngine.ScreenPaperAsync(chat, EvaluatedPaper, Inclusion, Exclusion, secondScreener: true);

        Assert.Equal("Included", answer.Decision);
        Assert.Null(answer.CriteriaParts);
        Assert.Contains(CriterionParts.Procedure.Trim(), prompts[0]);
        Assert.Contains(CriterionParts.AnswerField.Trim(), prompts[0]);
    }

    [Fact]
    public void APartMustComeFromTheCriterionAndAnIncludedRecordMayNotMissOne()
    {
        ScreeningAnswer With(string decision, string part, string met) => new()
        {
            Decision = decision, Reasoning = "r",
            CriteriaParts = new() { new CriterionCheck { Part = part, Met = met } },
        };
        Assert.Null(CriterionParts.Problem(With("Included", "With some form of  evaluation", "yes"), Inclusion));
        Assert.Null(CriterionParts.Problem(With("Excluded", "with some form of evaluation", "no"), Inclusion));
        Assert.Equal("A record that does not meet a part of the inclusion thresholds cannot be Included.",
            CriterionParts.Problem(With("Included", "with some form of evaluation", "no"), Inclusion));
        Assert.Contains("is not in the inclusion thresholds", CriterionParts.Problem(With("Included", "a proposed framework", "yes"), Inclusion));
        Assert.Equal("met must be \"yes\", \"no\" or \"unclear\".", CriterionParts.Problem(With("Included", "with some form of evaluation", "maybe"), Inclusion));
    }

    [Fact]
    public async Task ThePartsOfBothScreeningsAreOnFile()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(p =>
                p.Contains("Evaluate the following academic paper") ? Answer("suggesting a focus on system evaluation") : "{}"),
            SourceFactory = _ => new OneRecordSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await Record.ExceptionAsync(() => engine.RunReviewAsync(runId, new ReviewRequest("agentic AI software frameworks", "o", Inclusion, Exclusion, 2,
            SelectedSources: new[] { "openalex" }, SearchStrings: Array.Empty<string>())));
        var line = Assert.Single(engine.LoadState(runId)!.Phases.Screening);

        Assert.Equal("Excluded", line.Decision);
        Assert.Equal(ExclusionReasons.TooLittleInformation, line.ExclusionReason);
        Assert.Equal(new[] { "yes", "unclear" }, line.CriteriaParts!.Select(p => p.Met));
        Assert.Equal(new[] { "yes", "unclear" }, line.SecondCriteriaParts!.Select(p => p.Met));
    }
}

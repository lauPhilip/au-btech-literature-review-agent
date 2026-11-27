using System.IO.Compression;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #561, from run 289a0d3e: the closing step failed its checks on every try, and the paper then had no answers to
/// its research questions, no conclusion, and no word about either. A finished paper now always answers every question,
/// keeps the answers the discussion call checked, assembles answers in code from the verified findings when no written
/// answer passed, and says in place of the conclusion why the closing parts are missing.
/// </summary>
public class ClosingFallbackTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "closing-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static readonly string[] Questions = { "How do agent loops recover from faults?", "How are agent actions audited?" };

    private const string ClosingMarker = "You are writing the sections that follow the discussion";
    private const string DiscussionMarker = "You are writing the Discussion section";

    // The closing step answers, but never with an answer that passes its checks (a part is always empty).
    private static string FailingClosing(string prompt) =>
        """{"limitations":"","implications":"Teams should add supervisor checks.","futureResearch":"","conclusion":""}""";

    private static string KeepsDiscussionAnswers(string prompt) => prompt.Contains(ClosingMarker) ? FailingClosing(prompt)
        : prompt.Contains(DiscussionMarker)
            ? """{"answers":["Supervisor checks let agent loops recover from faults [1].","Governance layers audit what agents do [2]."],"text":"Recovery and audits complement each other [1, 2]."}"""
            : ReviewWriterTests.Respond(prompt);

    // Neither the discussion nor the closing step writes anything that passes; the extraction tags its finding with RQ1.
    private static string NothingWritten(string prompt) => prompt.Contains(ClosingMarker) ? FailingClosing(prompt)
        : prompt.Contains(DiscussionMarker) ? """{"answers":[],"text":""}"""
        : prompt.Contains("You are extracting data from one study")
            ? """{"studyType":"design science artefact with evaluation","method":{"value":"Supervisor checks","quote":"Agent loops recover from faults through supervisor checks."},"sample":{"value":"not reported","quote":""},"keyFindings":[{"value":"loops recover from faults through supervisor checks","quote":"Agent loops recover from faults through supervisor checks.","question":"RQ1"}],"limitations":{"value":"not reported","quote":""},"appraisalCategory":"quantitative_descriptive","appraisal":[{"id":"4.1","answer":"yes","quote":"Agent loops recover from faults through supervisor checks."}]}"""
            : ReviewWriterTests.Respond(prompt);

    private async Task<(PrismaReport Report, string Folder, string Tex)> RunAsync(Func<string, string> respond)
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, ResearchQuestions: Questions));
        string folder = Path.Join(_root, runId.ToString("N"));
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        return (report, folder, reader.ReadToEnd());
    }

    [Fact]
    public async Task TheDiscussionsCheckedAnswersAreKeptWhenTheClosingStepFails()
    {
        var (report, folder, tex) = await RunAsync(KeepsDiscussionAnswers);

        Assert.Equal(new[] { 1, 2 }, report.ResearchAnswers.Select(a => a.Number));
        Assert.StartsWith("Supervisor checks let agent loops recover from faults", report.ResearchAnswers[0].Answer);
        Assert.StartsWith("Governance layers audit what agents do", report.ResearchAnswers[1].Answer);
        Assert.Equal(PrismaReviewEngine.ClosingUnwrittenText(attempted: true, hasQuestions: true, answersFromEvidence: false), report.ConclusionItem);
        Assert.Contains("\"researchAnswer1\"", File.ReadAllText(Path.Join(folder, "citation-audit.json")));

        // main.tex no longer goes from the Discussion to the Declarations: the answers, then the plain sentence.
        int conclusion = tex.IndexOf(@"\section{Conclusion}", StringComparison.Ordinal);
        Assert.True(conclusion > tex.IndexOf(@"\section{Discussion}", StringComparison.Ordinal));
        Assert.True(conclusion < tex.IndexOf(@"\textbf{RQ1. How do agent loops recover from faults?}", StringComparison.Ordinal));
        Assert.Contains("could not be written: the language model's answer failed the checks in code on every try", tex);
        Assert.Contains("closing parts were not written", File.ReadAllText(Path.Join(folder, "thematic-codebook.json")));

        // The self-check finds every question answered, and the missing parts owned up to.
        var check = SystematicSelfCheck.Load(folder)!;
        Assert.Equal(CheckOutcome.Pass, check.Checks.Single(c => c.Id == "answers").Outcome);
        Assert.NotEqual(CheckOutcome.Fault, check.Checks.Single(c => c.Id == "parts").Outcome);
    }

    [Fact]
    public async Task WithoutAnyWrittenAnswerEveryQuestionIsAnsweredFromTheVerifiedFindings()
    {
        var (report, folder, tex) = await RunAsync(NothingWritten);

        Assert.Equal(2, report.ResearchAnswers.Count);
        Assert.StartsWith("This answer is not a synthesis: it lists the verified findings on this question, from the theme \"Recovery and oversight\"", report.ResearchAnswers[0].Answer);
        Assert.Matches(@"Study \[\d+\] reports: Loops recover from faults through supervisor checks\.", report.ResearchAnswers[0].Answer);
        Assert.Equal(AnswersFromEvidence.NoFindingText, report.ResearchAnswers[1].Answer);
        Assert.Contains(AnswersFromEvidence.AssembledSentence, report.ConclusionItem);
        Assert.Contains("assembled in code from the verified findings in the data extraction (Table A.2)", tex);
        Assert.Contains("\"researchAnswer1\"", File.ReadAllText(Path.Join(folder, "citation-audit.json")));
        Assert.Contains("answers to the research questions were assembled in code", File.ReadAllText(Path.Join(folder, "thematic-codebook.json")));
        Assert.NotEqual(CheckOutcome.Fault, SystematicSelfCheck.Load(folder)!.Checks.Single(c => c.Id == "answers").Outcome);
    }

    [Fact]
    public void ThePaperThatAnswersNoQuestionFailsTheSelfCheck()
    {
        // The paper of run 289a0d3e: two questions and no answers. The self-check must call that a fault.
        var input = new PaperCheckInput
        {
            Questions = Questions.Select((q, i) => new PaperQuestion($"RQ{i + 1}", q, "")).ToList(),
            Parts = { new("discussionItem", "Discussion", PaperPartKind.Discussion, "Recovery and audits complement each other [1, 2].") },
        };
        var check = PaperSelfCheck.Run(input).Checks.Single(c => c.Id == "answers");
        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Equal(2, check.Evidence.Count(e => e.Detail == "The paper gives no answer to this question."));
    }

    [Fact]
    public void FindingsWithFiguresComeFirstAndAnswersStayShort()
    {
        var book = new ThematicCodebook { Themes = { new Theme { Id = "T1", Name = "Evaluation", Studies = { 1, 2 }, Questions = new() { "RQ1" } } } };
        StudyExtraction Study(int reference, params (string Value, string Question)[] findings) => new()
        {
            ReferenceNumber = reference,
            KeyFindings = findings.Select(f => new EvidencedValue { Value = f.Value, Quote = f.Value, QuoteVerified = true, Question = f.Question }).ToList(),
        };
        var extractions = new[]
        {
            Study(1, ("evaluated in simulated environments", "RQ1"), ("38% productivity and 41% defect reduction", "RQ1"), ("governance agents", "none")),
            Study(2, Enumerable.Range(1, 6).Select(i => ($"finding {i}", "RQ1")).ToArray()),
            Study(3, ("outside every theme", "RQ1")),
        };
        string answer = AnswersFromEvidence.Write(new[] { "How are they evaluated?" }, book, extractions).Single();

        // Issue #727: the answer says what it is, and each finding is what its study reports, not a bare fragment.
        Assert.StartsWith("This answer is not a synthesis: it lists the verified findings on this question, from the theme \"Evaluation\", as the studies state them. " +
                          "Study [1] reports: 38% productivity and 41% defect reduction.", answer);
        Assert.Equal(AnswersFromEvidence.MaxFindings, System.Text.RegularExpressions.Regex.Matches(answer, @"\[\d\]").Count);
        Assert.DoesNotContain("governance agents", answer);
        Assert.DoesNotContain("outside every theme", answer);
    }
}

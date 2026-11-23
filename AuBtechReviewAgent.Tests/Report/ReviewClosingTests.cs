using System.IO.Compression;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The closing sections of a systematic review: the checks on limitations, implications, future research, the
/// conclusion and the answers to the research questions, and how they reach the report, the citation check and main.tex.
/// </summary>
public class ReviewClosingTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "paper-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public void AClosingAnswerIsRejectedWhenAPartIsMissingTooLongOrUnfounded()
    {
        const string known = "Agent loops recover through supervisor checks [1]. 2 studies were included.";
        Assert.Null(ReviewWriter.ClosingProblem("Two studies only [1].", "Use supervisors [1].", "Test at scale [2].", "Loops recover [1, 2].", known, 2));
        Assert.Contains("futureResearch, conclusion must not be empty", ReviewWriter.ClosingProblem("a", "b", " ", "", known, 2));
        Assert.Contains("conclusion is too long", ReviewWriter.ClosingProblem("a", "b", "c", string.Join(" ", Enumerable.Repeat("word", ReviewWriter.MaxConclusionWords + 1)), known, 2));
        Assert.Contains("citation numbers 7 are not in the reference list", ReviewWriter.ClosingProblem("a [7].", "b", "c", "d", known, 2));
        Assert.Contains("the text uses 85", ReviewWriter.ClosingProblem("a", "85 percent adopt it [1].", "c", "d", known, 2));
        Assert.Null(ReviewWriter.ClosingProblem("2 studies were included.", "b", "c", "d", known, 2)); // a number from the facts is fine
    }

    [Fact]
    public void AnUncitedSentenceThatReportsAFindingIsSentBack()
    {
        // From the audit of two real runs: the review's own readings carried citations, and the check flagged them.
        const string facts = "13 studies; full text was read for 9.";
        string? Problem(string limitations, string futureResearch, string conclusion) =>
            ReviewWriter.ClosingProblem(limitations, "Teams should add supervisor checks.", futureResearch, conclusion, facts, 13, ownFacts: facts);

        Assert.Null(Problem("The evidence rests on 13 case studies.", "Longitudinal field studies of supervised loops are missing.", "Safety needs both recovery and oversight."));
        Assert.Contains("conclusion: \"Studies report that oversight works.\" has no citation", Problem("a [1].", "c", "Studies report that oversight works."));
        Assert.Contains("futureResearch: \"Tests reach 40 to 90 percent coverage.\" has no citation", Problem("a [1].", "Tests reach 40 to 90 percent coverage.", "d"));
        Assert.Contains("names a study design", ReviewWriter.ClosingProblem("a [1].", "Run a randomised comparison first.", "c", "d", facts, 13, ownFacts: facts));
    }

    [Fact]
    public void AnswersToTheResearchQuestionsAreCheckedLikeTheOtherClosingParts()
    {
        const string known = "Agent loops recover through supervisor checks [1]. 2 studies were included.";
        string? Problem(params string[] answers) => ReviewWriter.ClosingProblem("a [1].", "b", "c", "d", known, 2, answers, 2);
        Assert.Null(Problem("Through supervisor checks [1].", "The results do not say [2]."));
        Assert.Contains("exactly 2 answer(s)", Problem("Only one [1]."));
        Assert.Contains("exactly 2 answer(s)", Problem("One [1].", " "));
        Assert.Contains("citation numbers 9 are not in the reference list", Problem("One [9].", "Two [1]."));
        Assert.Contains("the text uses 85", Problem("85 percent adopt it [1].", "Two [1]."));
        Assert.Null(ReviewWriter.ClosingProblem("a", "b", "c", "d", known, 2)); // without questions nothing changes
    }

    [Fact]
    public void AConclusionThatRestatesAnAnswerIsRejected()
    {
        // Issue #117: with research questions, the conclusion repeated the answers printed just before it.
        const string known = "Supervisor checks let agent loops recover from faults [1]. Governance layers audit what agents do [2].";
        string[] answers = { "Supervisor checks let agent loops recover from faults [1].", "Governance layers audit what agents do [2]." };

        const string repeats = "Agent loops recover from faults through supervisor checks, and governance layers audit their actions [1, 2].";
        Assert.Equal<(int, int)?>((1, 1), ReviewWriter.RepeatedAnswer(repeats, answers));
        Assert.Equal<(int, int)?>((2, 2), ReviewWriter.RepeatedAnswer("Safety needs more than one safeguard [1]. Governance layers audit what agents do [2].", answers));
        Assert.Contains("conclusion sentence 1 restates the answer to RQ1", ReviewWriter.ClosingProblem("a [1].", "b", "c", repeats, known, 2, answers, 2));

        const string drawsTogether = "Together, the themes suggest that safe agent loops need both recovery and oversight, and that support for either is still thin.";
        Assert.Null(ReviewWriter.RepeatedAnswer(drawsTogether, answers));
        Assert.Null(ReviewWriter.ClosingProblem("a [1].", "b", "c", drawsTogether, known, 2, answers, 2));
        Assert.Null(ReviewWriter.ClosingProblem("a [1].", "b", "c", repeats, known, 2)); // without questions there is nothing to repeat
    }

    [Fact]
    public void WithResearchQuestionsTheConclusionTakesNoCitations()
    {
        // Issue #124, from run 959b875d: the conclusion after the answers cited six or seven studies for the review's own reading.
        const string known = "Supervisor checks let agent loops recover from faults [1]. Governance layers audit what agents do [2].";
        string[] answers = { "Supervisor checks let agent loops recover from faults [1].", "Governance layers audit what agents do [2]." };
        const string cited = "Together, these findings indicate that the identified mechanisms are internally consistent [1, 2].";

        Assert.Contains("the conclusion takes no citations", ReviewWriter.ClosingProblem("a [1].", "b", "c", cited, known, 2, answers, 2));
        Assert.Null(ReviewWriter.ClosingProblem("a [1].", "b", "c", "Together, these findings indicate that the mechanisms fit together.", known, 2, answers, 2));
        Assert.Null(ReviewWriter.ClosingProblem("a [1].", "b", "c", cited, known, 2)); // without questions the conclusion is cited as before
    }

    [Fact]
    public void TheReviewsOwnCountsTakeNoCitation()
    {
        // Issue #116, from run 959b875d: "The evidence base rests on 13 studies, ... 8 are not empirical ... [8]."
        const string facts = "13 studies; full text was read for 9 and only the abstract for 4; 8 are not empirical (position papers, system descriptions or reviews).";
        const string results = "PANDA keeps 100% task completion under faults [9]. Supervisors restart 4 failed workers [7].";

        Assert.Equal("The evidence base rests on 13 studies, and 8 are not empirical [8].",
            ReviewWriter.OwnCountCited("Evidence is thin. The evidence base rests on 13 studies, and 8 are not empirical [8].", facts, results));
        Assert.NotNull(ReviewWriter.OwnCountCited("Only 9 were read in full text [2, 7].", facts, results));
        Assert.Null(ReviewWriter.OwnCountCited("The evidence base rests on 13 studies, and 8 are not empirical.", facts, results)); // not cited
        Assert.Null(ReviewWriter.OwnCountCited("Supervisors restart 4 failed workers [7].", facts, results)); // a number from the literature
        Assert.Null(ReviewWriter.OwnCountCited("One trial ran 13 agents [3].", facts, results)); // not a count of sources
        Assert.Null(ReviewWriter.OwnCountCited("Only 9 were read in full text [2].", "", results)); // no facts, nothing to compare

        string? problem = ReviewWriter.ClosingProblem("The evidence rests on 13 studies [8].", "b", "c", "d", facts + " " + results, 13, ownFacts: facts, results: results);
        Assert.Contains("limitations: \"The evidence rests on 13 studies [8].\" states this review's own count", problem);
    }

    [Fact]
    public void AnOwnCountInWordsThatCarriesACitationIsCaught()
    {
        // From the audit: a limitation read "The evidence base rests on four studies [1, 2, 3, 4]."
        Assert.Equal("The evidence base rests on four studies [1, 2, 3, 4].",
            ReviewWriter.OwnCountCited("The evidence base rests on four studies [1, 2, 3, 4].", "4 studies; 3 read in full text."));
        Assert.Null(ReviewWriter.OwnCountCited("The evidence base rests on four studies [1, 2, 3, 4].", "13 studies."));
    }

    [Fact]
    public void TheAnswersComeBetweenTheClosingSectionsAndTheConclusionInReadingOrder()
    {
        var themes = new[] { "theme1" };
        int Rank(string field) => CitationView.FieldRank(field, themes);
        Assert.True(Rank("futureResearchItem") < Rank("researchAnswer1"));
        Assert.True(Rank("researchAnswer1") < Rank("researchAnswer4"));
        Assert.True(Rank("researchAnswer4") < Rank("conclusionItem"));
        Assert.True(Rank("conclusionItem") < Rank("somethingElse"));
    }

    private static string RespondWithClosing(string prompt) => prompt.Contains("You are writing the sections that follow the discussion")
        ? """{"limitations":"Both studies leave long-term effects unmeasured [1, 2].","implications":"Teams should add supervisor checks to agent loops [1].","futureResearch":"Governance layers need evaluation in production [2].","conclusion":"Agent loops recover from faults through supervisor checks, and governance layers audit their actions [1, 2]."}"""
        : ReviewWriterTests.Respond(prompt);

    [Fact]
    public async Task TheClosingSectionsReachTheReportTheCitationCheckAndMainTex()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(RespondWithClosing),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        string folder = Path.Join(_root, runId.ToString("N"));
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;
        Assert.Equal("Both studies leave long-term effects unmeasured [1, 2].", report.EvidenceLimitationsItem);
        Assert.Equal("Teams should add supervisor checks to agent loops [1].", report.ImplicationsItem);
        Assert.Equal("Governance layers need evaluation in production [2].", report.FutureResearchItem);
        Assert.StartsWith("Agent loops recover from faults through supervisor checks", report.ConclusionItem);
        Assert.StartsWith("This review has limits of its own.", report.ReviewLimitationsItem);

        // Every closing part went through the citation check like the discussion.
        string audit = File.ReadAllText(Path.Join(folder, "citation-audit.json"));
        foreach (var field in new[] { "evidenceLimitationsItem", "implicationsItem", "futureResearchItem", "conclusionItem" })
            Assert.Contains($"\"{field}\"", audit);

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string tex = reader.ReadToEnd();
        foreach (var heading in new[] { @"\subsection{Limitations of the Evidence}", @"\subsection{Limitations of the Review Process}",
                     @"\subsection{Implications for Practice and Policy}", @"\subsection{Directions for Future Research}", @"\section{Conclusion}" })
            Assert.Contains(heading, tex);
        Assert.True(tex.IndexOf(@"\section{Discussion}", StringComparison.Ordinal) < tex.IndexOf(@"\section{Conclusion}", StringComparison.Ordinal));
        Assert.True(tex.IndexOf(@"\section{Conclusion}", StringComparison.Ordinal) < tex.IndexOf(@"\section{Declarations}", StringComparison.Ordinal));
    }

    private static string RespondWithAnswers(string prompt) => prompt.Contains("You are writing the sections that follow the discussion")
        ? """{"limitations":"Both studies leave long-term effects unmeasured [1, 2].","implications":"Teams should add supervisor checks to agent loops [1].","futureResearch":"Governance layers need evaluation in production [2].","conclusion":"Together, the themes suggest that safe agent loops need both recovery and oversight, and that support for either is still thin.","answers":["Supervisor checks let agent loops recover from faults [1].","Governance layers audit what agents do [2]."]}"""
        : ReviewWriterTests.Respond(prompt);

    [Fact]
    public async Task EachResearchQuestionIsInTheProtocolAndAnsweredInTheConclusion()
    {
        var model = new FakeChatService().RespondsWith(RespondWithAnswers);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        var questions = new[] { "How do agent loops recover from faults?", "How are agent actions audited?" };
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, ResearchQuestions: questions));

        // The questions are on file before the search, and kept with the run.
        string folder = Path.Join(_root, runId.ToString("N"));
        Assert.Equal(questions, engine.LoadState(runId)!.ResearchQuestions);
        string protocol = File.ReadAllText(Path.Join(folder, "protocol.md"));
        Assert.Contains("- Research question 1 (RQ1): How do agent loops recover from faults?", protocol);
        Assert.Contains("- Research question 2 (RQ2): How are agent actions audited?", protocol);
        Assert.Contains(model.Prompts, p => p.Contains("RQ2: How are agent actions audited?") && p.Contains("exactly 2 answer(s)"));
        Assert.Contains(model.Prompts, p => p.Contains("exactly 2 answer(s)") && p.Contains("the conclusion must not restate them"));

        // One checked answer per question, in order.
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;
        Assert.Equal(new[] { 1, 2 }, report.ResearchAnswers.Select(a => a.Number));
        Assert.Equal(questions, report.ResearchAnswers.Select(a => a.Question));
        Assert.StartsWith("Supervisor checks let agent loops recover from faults", report.ResearchAnswers[0].Answer);
        Assert.StartsWith("Governance layers audit what agents do", report.ResearchAnswers[1].Answer);
        string audit = File.ReadAllText(Path.Join(folder, "citation-audit.json"));
        Assert.Contains("\"researchAnswer1\"", audit);
        Assert.Contains("\"researchAnswer2\"", audit);

        // main.tex lists the questions in the method and answers them at the start of the Conclusion.
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string tex = reader.ReadToEnd();
        Assert.Contains(@"\subsection{Research Questions}", tex);
        Assert.Contains(@"\textbf{RQ2.} How are agent actions audited?", tex);
        int conclusion = tex.IndexOf(@"\section{Conclusion}", StringComparison.Ordinal);
        int firstAnswer = tex.IndexOf(@"\textbf{RQ1. How do agent loops recover from faults?}", StringComparison.Ordinal);
        int conclusionText = tex.IndexOf("Together, the themes suggest that safe agent loops need both recovery and oversight", StringComparison.Ordinal);
        Assert.True(conclusion >= 0 && conclusion < firstAnswer && firstAnswer < conclusionText);
    }

    [Fact]
    public async Task WithoutResearchQuestionsTheClosingPromptAsksForNoAnswers()
    {
        var model = new FakeChatService().RespondsWith(RespondWithClosing);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        string closingPrompt = model.Prompts.First(p => p.Contains("You are writing the sections that follow the discussion"));
        Assert.DoesNotContain("RESEARCH QUESTIONS", closingPrompt);
        Assert.DoesNotContain("\"answers\"", closingPrompt);
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.Empty(report.ResearchAnswers);
        Assert.DoesNotContain("Research question", File.ReadAllText(Path.Join(_root, runId.ToString("N"), "protocol.md")));
    }
}

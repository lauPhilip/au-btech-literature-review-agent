using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The paper of a review as one shared shape (module design M9). A whole systematic review runs offline with the
/// fake model of <see cref="ReviewWriterTests"/>, and its main.tex is compared, line for line, with the main.tex the
/// systematic review wrote before the shape was shared (Golden/systematic-main.tex). Dates, times and the protocol
/// hash change from run to run, so they are blanked in both. Set UPDATE_GOLDEN=1 to write the file again after a
/// deliberate change.
/// </summary>
public class ReviewPaperTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "paper-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string GoldenPath([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", "systematic-main.tex");

    /// <summary>The main.tex of one offline systematic review, with what changes between runs blanked.</summary>
    internal static async Task<string> SystematicMainTexAsync(string root)
    {
        var model = new FakeChatService().RespondsWith(ReviewWriterTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string tex = reader.ReadToEnd().Replace("\r\n", "\n");
        tex = Regex.Replace(tex, @"\d{4}-\d{2}-\d{2}( \d{2}:\d{2}(:\d{2})?)?", "DATE");
        tex = Regex.Replace(tex, @"Protocol Hash: \S+", "Protocol Hash: HASH");
        tex = Regex.Replace(tex, @"fingerprint [0-9a-f]{16}", "fingerprint FINGERPRINT");
        tex = Regex.Replace(tex, @"version [^ ]+\)", "version VERSION)");
        tex = Regex.Replace(tex, @"This run used version .*?(?= The run archive)", "This run used VERSION.");
        return tex;
    }

    [Fact]
    public async Task TheSystematicReviewWritesTheSameMainTexAsBefore()
    {
        string tex = await SystematicMainTexAsync(_root);

        Assert.Contains(@"\section{Results \& Synthesis}", tex);
        Assert.Contains(@"\subsection{Recovery and oversight}", tex);
        Assert.Contains("PRISMA 2020 flow diagram", tex);

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tex);
        }
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), tex);
    }

    [Fact]
    public void AnotherKindOfReviewFillsTheSameShapeWithItsOwnParts()
    {
        var paper = new ReviewPaper
        {
            KindLabel = "AI-GENERATED GREY LITERATURE REVIEW",
            Title = "Context engineering for LLM agents",
            ProtocolHash = "9c00331882d1",
            GeneratedAt = "2026-10-08 12:00:00 UTC",
            Abstract = "We reviewed 25 sources.",
            Rationale = "Agents need context [1].",
            Objectives = "To map the practices.",
            Methods = { new("Planning (G1–G5)", "The protocol was written before any search."), new("Quality (G11)", "", @"\textbf{Table 7} checklist") },
            DataHeading = "Data",
            DataText = "123 sources were found.",
            DataLatex = "\\begin{figure}[H]FLOW\\end{figure}\n",
            ResultsHeading = "Results by research question",
            ResultsText = "Five questions.",
            Themes = { new("RQ1: Retrieval & memory", "Sources fetch only what is needed [1, 2].") },
            ResultsLatex = { "MAP\n" },
            Discussion = "State of the art against the state of the practice [2].",
            Declarations = { new("Use of AI", "A language model wrote the sections; code checked every citation.") },
            References = { "Ana (2025). *Keeping context short*. blog.example.org", "Bo (2026). Retrieval first_v2." },
        };

        string tex = PaperLatex.Build(paper);

        Assert.Contains("AI-GENERATED GREY LITERATURE REVIEW", tex);
        Assert.Contains(@"\subsection{Planning (G1–G5)}", tex);
        Assert.Contains("\\subsection{Quality (G11)}\n\\textbf{Table 7} checklist".Replace("\n", Environment.NewLine), tex); // LaTeX built in code is kept as it is
        Assert.Contains(@"\section{Data}", tex);
        Assert.Contains(@"\subsection{RQ1: Retrieval \& memory}", tex); // plain text is escaped
        Assert.Contains($"\\end{{multicols}}{Environment.NewLine}MAP\n\\begin{{multicols}}{{2}}", tex); // a wide block leaves the columns
        Assert.Contains(@"\bibitem{ref1} Ana (2025). \textit{Keeping context short}. blog.example.org", tex);
        Assert.Contains(@"\bibitem{ref2} Bo (2026). Retrieval first\_v2", tex);
        Assert.DoesNotContain("PRISMA", tex);
    }

    [Fact]
    public void AClosingAnswerIsRejectedWhenAPartIsMissingTooLongOrUnfounded()
    {
        const string known = "Agent loops recover through supervisor checks [1]. 2 studies were included.";
        Assert.Null(ReviewWriter.ClosingProblem("Two studies only [1].", "Use supervisors [1].", "Test at scale [2].", "Loops recover [1, 2].", known, 2));
        Assert.Contains("futureResearch, conclusion must not be empty", ReviewWriter.ClosingProblem("a", "b", " ", "", known, 2));
        Assert.Contains("conclusion is too long", ReviewWriter.ClosingProblem("a", "b", "c", string.Join(" ", Enumerable.Repeat("word", ReviewWriter.MaxConclusionWords + 1)), known, 2));
        Assert.Contains("citation numbers 7 are not in the reference list", ReviewWriter.ClosingProblem("a [7].", "b", "c", "d", known, 2));
        Assert.Contains("the text uses 85", ReviewWriter.ClosingProblem("a", "85 percent adopt it.", "c", "d", known, 2));
        Assert.Null(ReviewWriter.ClosingProblem("2 studies were included.", "b", "c", "d", known, 2)); // a number from the facts is fine
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
        Assert.Contains("the text uses 85", Problem("85 percent adopt it.", "Two [1]."));
        Assert.Null(ReviewWriter.ClosingProblem("a", "b", "c", "d", known, 2)); // without questions nothing changes
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
        ? """{"limitations":"The evidence rests on two studies read from their abstracts [1, 2].","implications":"Teams should add supervisor checks to agent loops [1].","futureResearch":"Governance layers need evaluation in production [2].","conclusion":"Agent loops recover from faults through supervisor checks, and governance layers audit their actions [1, 2]."}"""
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
        Assert.Equal("The evidence rests on two studies read from their abstracts [1, 2].", report.EvidenceLimitationsItem);
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
        ? """{"limitations":"The evidence rests on two studies read from their abstracts [1, 2].","implications":"Teams should add supervisor checks to agent loops [1].","futureResearch":"Governance layers need evaluation in production [2].","conclusion":"Agent loops recover from faults through supervisor checks, and governance layers audit their actions [1, 2].","answers":["Supervisor checks let agent loops recover from faults [1].","Governance layers audit what agents do [2]."]}"""
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
        int conclusionText = tex.IndexOf("Agent loops recover from faults through supervisor checks, and", StringComparison.Ordinal);
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

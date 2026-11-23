using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole systematic review with two research questions (issue #124): the codebook, the theme subsection, the
/// discussion, the peer-review critique and the front matter all see the questions; the objectives are written in code;
/// and the question no code bears on is answered in code while the model answers only the other. The run without
/// research questions is pinned word for word by Golden/systematic-writing-prompts.txt.
/// </summary>
public class ResearchQuestionWritingTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "rq-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static readonly string[] Questions = { "How do agent loops recover from faults?", "How are agent loops certified?" };
    private const string Answer = "Agent loops recover through supervisor checks [1].";

    private static string Respond(string prompt)
    {
        if (prompt.Contains("You are building the codebook"))
            return """{"themes":[{"name":"Recovery and oversight","description":"How agent loops recover and are overseen.","codes":["C1","C2"],"questions":["RQ1"]}],"unassigned":[],"unanswered":[{"question":"RQ2","reason":"No study certifies agent loops."}]}""";
        // The discussion call writes the answer to RQ1 first (issue #446).
        if (prompt.Contains("You are writing the Discussion section"))
            return $$"""{"answers":["{{Answer}}"],"text":"Recovery and audits complement each other [1, 2]."}""";
        if (prompt.Contains("You are writing the sections that follow the discussion of"))
            return """{"limitations":"The evidence rests on abstracts only [1].","implications":"Teams should add supervisor checks [1].","futureResearch":"Certification of agent loops needs study [2].","conclusion":"Safe agent loops need recovery and oversight, while certification is still open."}""";
        return ReviewWriterTests.Respond(prompt);
    }

    [Fact]
    public async Task EveryWritingStepSeesTheQuestionsAndTheUnansweredOneIsAnsweredInCode()
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
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, ResearchQuestions: Questions));

        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.Select(p => p.Replace("\r\n", "\n")).ToList();
        string Prompt(string marker) => Assert.Single(prompts, p => p.Contains(marker));
        string aim = ReviewWriter.AimBlock("Map agent loop safety", Questions);
        Assert.Contains("RQ2: How are agent loops certified?", aim);
        foreach (var marker in new[] { "You are building the codebook", "You are writing the Discussion section", "strict but constructive peer reviewer", "You are writing the title, abstract, rationale and objectives" })
            Assert.Contains(aim, Prompt(marker));
        Assert.Contains("THEME: Recovery and oversight. How agent loops recover and are overseen.\nTHIS THEME ANSWERS: RQ1: How do agent loops recover from faults?\n",
            Prompt("You are writing one subsection of the Results"));

        // The front matter does not ask for the objectives: they are written in code from the protocol.
        string front = Prompt("You are writing the title, abstract, rationale and objectives");
        Assert.Contains("4. \"objectives\" are written in code from the research questions above; do not write them.", front);
        Assert.EndsWith("{\"title\":\"...\",\"abstract\":\"...\",\"rationale\":\"... [n] ...\"}", front.TrimEnd());
        // It is written after the closing sections, from the answers and the conclusion as given text (issue #446).
        Assert.Contains($"\nRQ1: {Answer}\nRQ2: {ReviewWriter.UnansweredText(WritingProfile.Systematic)}\n\nCONCLUSION (written and checked before this step; the abstract ends on the same reading, in its own words):\nSafe agent loops need recovery and oversight, while certification is still open.\n", front);
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.Equal("The review set out to answer two research questions: (RQ1) How do agent loops recover from faults?; (RQ2) How are agent loops certified?", report.ObjectivesItem);

        // The discussion call answers RQ1 first (issue #446); RQ2, which no code bears on, is answered in code.
        string discussion = Prompt("You are writing the Discussion section");
        Assert.Contains("\"answers\": exactly 1 answer(s), one for each of RQ1 in that order", discussion);
        Assert.Contains("No evidence bears on RQ2, so it is answered in code; give no answer for it.", discussion);

        // The closing step gets both answers as given text and asks for none; RQ2's stays in the codebook.
        string closing = Prompt("You are writing the sections that follow the discussion of");
        Assert.Contains($"RQ1: How do agent loops recover from faults?\nAnswer: {Answer}\nRQ2: How are agent loops certified?\nAnswer: {ReviewWriter.UnansweredText(WritingProfile.Systematic)}\n", closing);
        Assert.DoesNotContain("\"answers\"", closing);
        Assert.Equal(new[] { Answer, ReviewWriter.UnansweredText(WritingProfile.Systematic) }, report.ResearchAnswers.Select(a => a.Answer));
        Assert.Equal(new[] { 1, 2 }, report.ResearchAnswers.Select(a => a.Number));
        var book = engine.LoadState(runId)!.ThematicSynthesis!;
        Assert.Equal(new[] { "RQ1" }, Assert.Single(book.Themes).Questions);
        Assert.Equal(new UnansweredQuestion("RQ2", "No study certifies agent loops."), Assert.Single(book.Unanswered!));
    }

    [Fact]
    public async Task WithoutQuestionsNoWritingPromptMentionsThem()
    {
        var model = new FakeChatService().RespondsWith(ReviewWriterTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.ToList();
        Assert.DoesNotContain(prompts, p => p.Contains("RESEARCH QUESTIONS") || p.Contains("THIS THEME ANSWERS"));
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.DoesNotContain("set out to answer", report.ObjectivesItem);
        Assert.Null(engine.LoadState(runId)!.ThematicSynthesis!.Unanswered);
    }
}

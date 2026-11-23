using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The codebook with research questions: every theme names the questions it answers, every question is answered by a
/// theme or listed as unanswered with a reason, and the themes come in question order. Without questions the prompt is
/// as before.
/// </summary>
public class ResearchQuestionCodebookTests
{
    private static readonly string[] Questions = { "How do agent loops recover?", "How are agent loops certified?" };

    private static List<ThematicCode> Codes() => new()
    {
        new() { Id = "C1", Reference = 1, Label = "fault recovery" },
        new() { Id = "C2", Reference = 2, Label = "auditing" },
    };

    [Fact]
    public void TheQuestionCheckRejectsUnknownMissingAndDoubleQuestions()
    {
        var ids = new[] { "RQ1", "RQ2" };
        Assert.Null(ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "RQ1" } }, new[] { "RQ2" }));
        Assert.Null(ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "rq1", "RQ2" }, new[] { "RQ2" } }, Array.Empty<string>()));
        Assert.Contains("unknown research questions: RQ3", ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "RQ1", "RQ3" } }, new[] { "RQ2" }));
        Assert.Contains("unknown research questions: RQ5", ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "RQ1" } }, new[] { "RQ2", "RQ5" }));
        Assert.Contains("RQ2 is in no theme and not listed as unanswered", ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "RQ1" } }, Array.Empty<string>()));
        Assert.Contains("every theme names the research questions", ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "RQ1" }, Array.Empty<string>() }, new[] { "RQ2" }));
        Assert.Contains("RQ1 is answered by a theme and also listed as unanswered", ThematicSynthesis.QuestionProblem(ids, new[] { new[] { "RQ1" } }, new[] { "RQ1", "RQ2" }));
    }

    [Fact]
    public async Task ThemesAnswerTheQuestionsInQuestionOrderAndAQuestionNoCodeBearsOnIsOnFile()
    {
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Audits","description":"d","codes":["C2"],"questions":["RQ3"],"singleStudyReason":"Only this study covers it."},{"name":"Recovery","description":"d","codes":["C1"],"questions":["RQ1"],"singleStudyReason":"Only this study covers it."}],"unassigned":[],"unanswered":[]}""")
            .Returns("""{"themes":[{"name":"Audits","description":"d","codes":["C2"],"questions":["RQ1"],"singleStudyReason":"Only this study covers it."},{"name":"Recovery","description":"d","codes":["C1"],"questions":["RQ1"],"singleStudyReason":"Only this study covers it."}],"unassigned":[],"unanswered":[{"question":"RQ2","reason":"No study certifies agent loops."}]}""");
        var unanswered = new List<UnansweredQuestion>();

        var (themes, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, questions: Questions, unanswered: unanswered);

        string first = chat.Prompts[0].Replace("\r\n", "\n");
        Assert.Contains("REVIEW OBJECTIVE: \"objective\"\nRESEARCH QUESTIONS (typed by the reviewer, in order;", first);
        Assert.Contains("RQ1: How do agent loops recover?\nRQ2: How are agent loops certified?", first);
        Assert.Contains("Organise the themes by research question", first);
        Assert.Contains("\"codes\":[\"C1\",\"C4\"],\"questions\":[\"RQ1\"]}],\"unassigned\":[{\"code\":\"C9\",\"reason\":\"...\"}],\"unanswered\":[{\"question\":\"RQ2\",\"reason\":\"...\"}]}", first);
        Assert.Contains("unknown research questions: RQ3", chat.Prompts[1]);
        Assert.Equal(new[] { "T1", "T2" }, themes.Select(t => t.Id));
        Assert.All(themes, t => Assert.Equal(new[] { "RQ1" }, t.Questions));
        Assert.Equal(new UnansweredQuestion("RQ2", "No study certifies agent loops."), Assert.Single(unanswered));
    }

    [Fact]
    public async Task ACodebookWithoutQuestionsIsSentBack()
    {
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1","C2"]}],"unassigned":[]}""")
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1","C2"],"questions":["RQ1","RQ2"]}],"unassigned":[]}""");

        var (themes, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, questions: Questions, unanswered: new List<UnansweredQuestion>());

        Assert.Contains("every theme names the research questions it answers", chat.Prompts[1]);
        Assert.Equal(new[] { "RQ1", "RQ2" }, Assert.Single(themes).Questions);
    }

    [Fact]
    public async Task ThemesAreSortedByTheirFirstQuestion()
    {
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Audits","description":"d","codes":["C2"],"questions":["RQ2"],"singleStudyReason":"Only this study covers it."},{"name":"Recovery","description":"d","codes":["C1"],"questions":["RQ1"],"singleStudyReason":"Only this study covers it."}],"unassigned":[]}""");
        var unanswered = new List<UnansweredQuestion>();

        var (themes, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, questions: Questions, unanswered: unanswered);

        Assert.Equal(new[] { ("T1", "Recovery", "RQ1"), ("T2", "Audits", "RQ2") }, themes.Select(t => (t.Id, t.Name, t.Questions![0])));
        Assert.Empty(unanswered);
    }

    [Fact]
    public async Task WithoutQuestionsTheCodebookPromptAndFileAreAsBefore()
    {
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Audits","description":"d","codes":["C2"],"singleStudyReason":"Only this study covers it."},{"name":"Recovery","description":"d","codes":["C1"],"singleStudyReason":"Only this study covers it."}],"unassigned":[]}""");

        var (themes, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8);

        string prompt = chat.Prompts[0].Replace("\r\n", "\n");
        Assert.Contains("REVIEW OBJECTIVE: \"objective\"\n", prompt);
        Assert.DoesNotContain("RESEARCH QUESTIONS", prompt);
        Assert.DoesNotContain("questions", prompt);
        Assert.Contains("{\"themes\":[{\"name\":\"...\",\"description\":\"...\",\"codes\":[\"C1\",\"C4\"]}],\"unassigned\":[{\"code\":\"C9\",\"reason\":\"...\"}]}", prompt);
        Assert.Equal(new[] { "Audits", "Recovery" }, themes.Select(t => t.Name)); // the model's order
        Assert.All(themes, t => Assert.Null(t.Questions));
        string json = System.Text.Json.JsonSerializer.Serialize(new ThematicCodebook { Themes = themes });
        Assert.DoesNotContain("Questions", json);
        Assert.DoesNotContain("Unanswered", json);
    }

    [Fact]
    public void TheAimBlockAndTheObjectivesFromTheQuestions()
    {
        Assert.Equal("REVIEW OBJECTIVE: \"Map safety\"", ReviewWriter.AimBlock("Map safety", null));
        Assert.Equal("REVIEW OBJECTIVE: \"Map safety\"", ReviewWriter.AimBlock("Map safety", Array.Empty<string>()));
        Assert.EndsWith("\nRQ1: How do agent loops recover?\nRQ2: How are agent loops certified?", ReviewWriter.AimBlock("Map safety", Questions));
        Assert.Null(ReviewWriter.ObjectivesFromQuestions(Array.Empty<string>()));
        Assert.Equal("The review set out to answer two research questions: (RQ1) How do agent loops recover?; (RQ2) How are agent loops certified?",
            ReviewWriter.ObjectivesFromQuestions(Questions));
        Assert.Equal("The review set out to answer one research question: (RQ1) Map recovery.", ReviewWriter.ObjectivesFromQuestions(new[] { "Map recovery" }));
    }
}

using System.Runtime.CompilerServices;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The coding prompt with the research questions and eligibility criteria (issue #430): a code that bears on a question
/// is kept and names it, unknown questions are sent back, the codebook sees each code's questions, and without
/// questions the coding prompt is as before (Golden/coding-prompt-no-questions.txt, written by the code before this
/// change; UPDATE_GOLDEN=1 rewrites it).
/// </summary>
public class QuestionDirectedCodingTests
{
    private static readonly string[] Questions = { "How is fault coverage measured?", "Which controls keep agents safe?" };
    private const string Criteria = "Include: studies of AI agents in production\nExclude: opinion pieces";

    private static StudyExtraction Extraction(int reference, string? question) => new()
    {
        ReferenceNumber = reference, StudyType = "experiment", EvidenceBasis = "full text",
        KeyFindings = { new EvidencedValue { Value = "Fault injection measured coverage", Quote = "Fault injection measured fault coverage.", QuoteVerified = true, Question = question } },
    };

    private static ReferencedPaper[] Papers() => new[] { new ReferencedPaper(1, "P1", "Paper 1", "Fault injection measured fault coverage.", Array.Empty<DocumentChunk>()) };

    [Fact]
    public async Task TheCodingPromptHasTheQuestionsAndCriteriaAndCodesCarryTheirQuestion()
    {
        var chat = new FakeChatService()
            .Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault coverage by injection","findings":["F1.1"],"questions":["RQ3"]}]}]}""")
            .Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault coverage by injection","findings":["F1.1"],"questions":["rq1"]}]}]}""");
        var (codes, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, "Safe agents", Papers(), new[] { Extraction(1, "RQ1") }, 6, 1,
            questions: Questions, criteria: Criteria);

        string prompt = chat.Prompts[0];
        Assert.Contains("RQ1: How is fault coverage measured?", prompt);
        Assert.Contains("RQ2: Which controls keep agents safe?", prompt);
        Assert.Contains("ELIGIBILITY CRITERIA", prompt);
        Assert.Contains("Exclude: opinion pieces", prompt);
        Assert.Contains("F1.1 [RQ1]: Fault injection measured coverage", prompt);
        Assert.Contains("never leave it out", prompt);
        Assert.Contains("unknown research questions (RQ3)", chat.Prompts[1]);
        Assert.Empty(uncoded);
        Assert.Equal(new[] { "RQ1" }, Assert.Single(codes).Questions);
    }

    [Fact]
    public async Task TheCodebookSeesEachCodesQuestions()
    {
        var codes = new List<ThematicCode>
        {
            new() { Id = "C1", Reference = 1, Label = "fault coverage", Questions = new() { "RQ1" } },
            new() { Id = "C2", Reference = 2, Label = "audit logging", Questions = new() },
        };
        var chat = new FakeChatService().Returns("""{"themes":[{"name":"Coverage","description":"d","codes":["C1"],"questions":["RQ1"]},{"name":"Controls","description":"d","codes":["C2"],"questions":["RQ2"]}],"unassigned":[],"unanswered":[]}""");
        await ThematicSynthesis.BuildCodebookAsync(chat, "Safe agents", codes, 4, "", Questions, new());

        Assert.Contains("C1 [study 1] (RQ1): fault coverage", chat.Prompts[0]);
        Assert.Contains("C2 [study 2]: audit logging", chat.Prompts[0]);
    }

    private static string GoldenPath([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", "coding-prompt-no-questions.txt");

    [Fact]
    public async Task ARunWithoutQuestionsSendsTheSameCodingPromptAsBefore()
    {
        var chat = new FakeChatService().Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault coverage","findings":["F1.1"]}]}]}""");
        var (codes, _) = await ThematicSynthesis.CodeStudiesAsync(chat, "Safe agents", Papers(), new[] { Extraction(1, null) }, 6, 1);
        Assert.Null(Assert.Single(codes).Questions);
        string prompt = chat.Prompts[0].Replace("\r\n", "\n");

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, prompt);
        }
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), prompt);
    }
}

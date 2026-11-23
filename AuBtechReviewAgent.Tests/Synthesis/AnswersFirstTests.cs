using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #446: the discussion call writes the answers to the research questions first and builds on them; the closing
/// sections and the front matter get the answers as given text, so the review states its reading once.
/// </summary>
public class AnswersFirstTests
{
    private static ReviewEvidence Evidence(params UncodedStudy[] leftOut) => new()
    {
        Sources = Enumerable.Range(1, 3).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}." }).ToList(),
        BaseFacts = "3 studies.",
        LeftOut = leftOut,
    };

    private static readonly List<SynthesisSection> Sections = new()
    {
        new SynthesisSection { Field = "synthesis-1", ThemeId = "T1", Heading = "Recovery", Text = "Supervisors restart failed agent steps [1, 2]." },
    };

    private static readonly string[] Questions = { "How do agent loops recover?", "How are agent loops certified?" };

    private const string Discussion = "Recovery and oversight work together [1, 2].";
    private const string Answer = "Agent loops recover through supervisors that restart failed steps [1, 2].";

    private static string Json(string text, params string[] answers) =>
        System.Text.Json.JsonSerializer.Serialize(answers.Length == 0 ? (object)new { text } : new { answers, text });

    [Fact]
    public async Task WithoutQuestionsThePromptIsTheDiscussionsOwnAndThereAreNoAnswers()
    {
        var plain = new FakeChatService().Returns(Json(Discussion));
        await ReviewWriter.WriteDiscussionAsync(plain, WritingProfile.Systematic, "agent loops", "Map agent loops", Sections, Evidence(), 3);
        var first = new FakeChatService().Returns(Json(Discussion));

        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(first, WritingProfile.Systematic, "agent loops", "Map agent loops", Sections, Evidence(), 3, null, null, null);

        Assert.Equal(plain.Prompts.Single(), first.Prompts.Single());
        Assert.Equal(Discussion, result.Text);
        Assert.Null(result.Answers);
    }

    [Fact]
    public async Task TheAnswersComeFirstAreCheckedAndTheUnansweredQuestionIsAnsweredInCode()
    {
        var model = new FakeChatService()
            .Returns(Json(Discussion, Answer, "Certification is open."))   // two answers for one question asked: sent back
            .Returns(Json(Discussion, "Agent loops recover [3]."))         // cites a study left out of the themes: sent back
            .Returns(Json(Discussion, Answer));
        var notes = new List<string>();

        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(model, WritingProfile.Systematic, "agent loops", "Map agent loops", Sections,
            Evidence(new UncodedStudy(3, "About vaping.")), 3, notes.Add, Questions, new[] { "RQ2" });

        string prompt = model.Prompts[0];
        Assert.Contains("5. Write the answers first. \"answers\": exactly 1 answer(s), one for each of RQ1 in that order", prompt);
        Assert.Contains("No evidence bears on RQ2, so it is answered in code; give no answer for it.", prompt);
        Assert.Contains("the discussion builds on the answers, says what they mean together and where the themes connect, and does not restate them either.", prompt);
        Assert.EndsWith("{\"answers\":[\"answer to RQ1\"],\"text\":\"the discussion with inline [n] citations\"}", prompt.TrimEnd());
        Assert.Contains("answers must hold exactly 1 answer(s), one per research question; you gave 2.", model.Prompts[1]);
        Assert.Contains("answer 1: it cites [3], which the synthesis left out of the themes", model.Prompts[2]);
        Assert.Equal(Discussion, result.Text);
        Assert.Equal(new[] { Answer, ReviewWriter.UnansweredText(WritingProfile.Systematic) }, result.Answers);
    }

    [Fact]
    public void AnAnswerWithANumberFromNowhereOrAnUncitedFindingIsSentBack()
    {
        string results = "### Recovery\nSupervisors restart 40 % of failed steps [1].";
        Assert.Null(ReviewWriter.AnswersProblem(new[] { "Supervisors restart 40 % of failed steps [1]." }, 1, Evidence(), results, 3, WritingProfile.Systematic));
        Assert.Contains("the answers use 70", ReviewWriter.AnswersProblem(new[] { "Supervisors restart 70 % of failed steps [1]." }, 1, Evidence(), results, 3, WritingProfile.Systematic));
        Assert.StartsWith("answer 1: \"Studies show that supervisors help.\" has no citation",
            ReviewWriter.AnswersProblem(new[] { "Studies show that supervisors help." }, 1, Evidence(), results, 3, WritingProfile.Systematic));
        Assert.Contains("citation numbers 9 are not in the reference list", ReviewWriter.AnswersProblem(new[] { "Supervisors help [9]." }, 1, Evidence(), results, 3, WritingProfile.Systematic));
    }

    private static string Closing(string conclusion) => System.Text.Json.JsonSerializer.Serialize(new
    {
        limitations = "The evidence rests on two studies.",
        implications = "Teams should add supervisors.",
        futureResearch = "Certification needs its own trials.",
        conclusion,
    });

    [Fact]
    public async Task TheClosingGetsTheAnswersAsGivenTextAndItsConclusionMayNotRestateThem()
    {
        var given = new[] { Answer, ReviewWriter.UnansweredText(WritingProfile.Systematic) };
        var model = new FakeChatService()
            .Returns(Closing("Agent loops recover through supervisors that restart failed steps."))   // restates the answer to RQ1: sent back
            .Returns(Closing("Safe agent loops need recovery first, and certification is still open."));

        var closing = await ReviewWriter.WriteClosingAsync(model, WritingProfile.Systematic, "agent loops", "Map agent loops", "Included: 3.", Sections, Discussion,
            Evidence(), 3, null, Questions, new[] { "RQ2" }, given);

        string prompt = model.Prompts[0];
        Assert.Contains("RESEARCH QUESTIONS AND THEIR ANSWERS", prompt);
        Assert.Contains($"RQ1: How do agent loops recover?\nAnswer: {Answer}\nRQ2: How are agent loops certified?\nAnswer: {given[1]}", prompt.Replace("\r\n", "\n"));
        Assert.DoesNotContain("\"answers\"", prompt);
        Assert.Contains("Do not restate the answers above", prompt);
        Assert.Contains("restates the answer to RQ1", model.Prompts[1]);
        Assert.Equal(given, closing!.Answers);
        Assert.Equal("Safe agent loops need recovery first, and certification is still open.", closing.Conclusion);
    }

    [Fact]
    public async Task TheFrontMatterGetsTheAnswersAndTheConclusion()
    {
        var model = new FakeChatService().Returns("""{"title":"Agent loops: a systematic review","abstract":"We included 3 studies. Supervisors restart failed steps.","rationale":"Agent loops fail in production [1]."}""");

        var front = await ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent loops", "Map agent loops", "Included: 3.", Sections, Discussion,
            Evidence().ReferenceLines(), 3, questions: Questions, answers: new[] { Answer, "None." }, conclusion: "Recovery comes first.");

        string prompt = model.Prompts.Single().Replace("\r\n", "\n");
        Assert.Contains($"ANSWERS TO THE RESEARCH QUESTIONS (written and checked before this step; the abstract's results and what they mean agree with them):\nRQ1: {Answer}\nRQ2: None.", prompt);
        Assert.Contains("CONCLUSION (written and checked before this step; the abstract ends on the same reading, in its own words):\nRecovery comes first.", prompt);
        Assert.NotNull(front);
    }
}

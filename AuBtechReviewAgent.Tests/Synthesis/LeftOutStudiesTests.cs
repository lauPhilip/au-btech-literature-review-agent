using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #166: studies the synthesis left out of every theme are named, with the reason, to the discussion and the
/// closing sections, which may not use them as evidence; the codebook gets the eligibility criteria the studies met.
/// </summary>
public class LeftOutStudiesTests
{
    private static ReviewEvidence Evidence(params UncodedStudy[] leftOut) => new()
    {
        Sources = Enumerable.Range(1, 3).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}." }).ToList(),
        BaseFacts = "3 studies.",
        LeftOut = leftOut,
    };

    private static readonly UncodedStudy Vaping = new(3, "Codes left out of the themes: evaluates a text message program, not a chatbot.");

    private static readonly List<SynthesisSection> Sections = new()
    {
        new SynthesisSection { Field = "synthesis-1", Heading = "Chatbots help people quit", Text = "Chatbots raised quit rates [1, 2]." },
    };

    [Fact]
    public void TheSystematicEvidenceCarriesTheStudiesInNoThemeWithTheirReason()
    {
        var book = new ThematicCodebook();
        book.Uncoded.Add(new UncodedStudy(7, "Codes left out of the themes: about vaping."));
        book.Uncoded.Add(new UncodedStudy(2, "No code bearing on the objective."));

        var evidence = SystematicEvidence.Build(book, new List<IncludedPaperMetricRow>(), new List<ReferencedPaper>(), new List<StudyExtraction>());

        Assert.Equal(new[] { 2, 7 }, evidence.LeftOut.Select(u => u.Reference));
        Assert.Equal("No code bearing on the objective.", evidence.LeftOut[0].Reason);
    }

    [Fact]
    public void ThePromptBlockIsEmptyWithoutLeftOutStudiesAndListsThemOtherwise()
    {
        Assert.Equal("", ReviewWriter.LeftOutBlock(Evidence(), WritingProfile.Systematic));

        string block = ReviewWriter.LeftOutBlock(Evidence(Vaping), WritingProfile.Systematic);
        Assert.StartsWith("\n\nSTUDIES LEFT OUT OF THE THEMES", block);
        Assert.Contains("do not cite them as evidence for any claim", block);
        Assert.EndsWith("[3] Codes left out of the themes: evaluates a text message program, not a chatbot.", block);
    }

    [Fact]
    public void CitingALeftOutStudyIsFound()
    {
        Assert.Null(ReviewWriter.LeftOutCited("Chatbots work [1, 3].", Evidence()));
        Assert.Null(ReviewWriter.LeftOutCited("Chatbots work [1, 2].", Evidence(Vaping)));
        Assert.Equal("it cites [3], which the synthesis left out of the themes; do not use it as evidence.",
            ReviewWriter.LeftOutCited("Chatbots work [1, 3].", Evidence(Vaping)));
    }

    [Fact]
    public async Task TheDiscussionIsToldWhichStudiesWereLeftOutAndSentBackWhenItCitesOne()
    {
        var chat = new FakeChatService()
            .Returns("""{"text":"Randomized evidence shows chatbots work for vaping too [2, 3]."}""")
            .Returns("""{"text":"Randomized evidence shows chatbots help smokers quit [1, 2]."}""");

        string discussion = await ReviewWriter.WriteDiscussionAsync(chat, WritingProfile.Systematic, "chatbots", "Map chatbots for quitting", Sections, Evidence(Vaping), 3);

        Assert.Equal("Randomized evidence shows chatbots help smokers quit [1, 2].", discussion);
        Assert.Contains("STUDIES LEFT OUT OF THE THEMES", chat.Prompts[0]);
        Assert.Contains("[3] Codes left out of the themes: evaluates a text message program", chat.Prompts[0]);
        Assert.Contains("it cites [3], which the synthesis left out of the themes", chat.Prompts[1]);
    }

    [Fact]
    public async Task WithoutLeftOutStudiesTheDiscussionPromptHasNoSuchBlock()
    {
        var chat = new FakeChatService().Returns("""{"text":"Chatbots help smokers quit [1, 2]."}""");

        await ReviewWriter.WriteDiscussionAsync(chat, WritingProfile.Systematic, "chatbots", "Map chatbots for quitting", Sections, Evidence(), 3);

        Assert.DoesNotContain("LEFT OUT OF THE THEMES", Assert.Single(chat.Prompts));
    }

    [Fact]
    public void OnlyTheLimitationsMayNameALeftOutStudy()
    {
        string known = "Chatbots raised quit rates [1, 2].";
        string? Problem(string limitations, string implications) =>
            ReviewWriter.ClosingProblem(limitations, implications, "Trials are needed [1].", "Chatbots help.", known, 3, evidence: Evidence(Vaping));

        Assert.Null(Problem("One study about text messages was left out of the themes [3].", "Teams should try chatbots [1]."));
        Assert.Equal("implications: it cites [3], which the synthesis left out of the themes; do not use it as evidence. Name them only in the limitations.",
            Problem("The evidence is thin [1].", "The evidence for vaping is firm [3]."));
    }

    [Fact]
    public async Task TheCodebookGetsTheEligibilityCriteriaWhenThereAreAny()
    {
        var codes = new List<ThematicCode> { new() { Id = "C1", Reference = 1, Label = "quit support" } };
        const string answer = """{"themes":[{"name":"Quit support","description":"d","codes":["C1"]}],"unassigned":[]}""";

        var withCriteria = new FakeChatService().Returns(answer);
        await ThematicSynthesis.BuildCodebookAsync(withCriteria, "objective", codes, 8, "Include: chatbots that help people stop smoking or vaping");
        var without = new FakeChatService().Returns(answer);
        await ThematicSynthesis.BuildCodebookAsync(without, "objective", codes, 8);

        Assert.Contains("ELIGIBILITY CRITERIA (every study below met them and was included):\nInclude: chatbots that help people stop smoking or vaping", withCriteria.Prompts[0]);
        Assert.Contains("Never leave a code out because its study is outside the topic", withCriteria.Prompts[0]);
        Assert.DoesNotContain("ELIGIBILITY CRITERIA", without.Prompts[0]);
        Assert.DoesNotContain("Never leave a code out", without.Prompts[0]);
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Regression tests from the recorded text of real run 289a0d3e (issues #561 and #572): the conclusion sentence the
/// closing step sent back three times, the theme sentence that dropped the verified figures, and the discussion that
/// retold the themes and read agreement into results that measure different things. Only the sentences are copied.
/// </summary>
public class ResultReadingTests
{
    // thematic-codebook.json, WritingNotes[1]: the sentence that failed the closing step's third try.
    private const string RunConclusion =
        "Taken together, safety rests on layered mechanisms rather than one safeguard. " +
        "These mechanisms are evaluated through a mix of simulated experiments, aggregated hardware-in-the-loop reviews, and long-term real-world deployments, though the empirical base remains narrow.";

    // prisma-report.json, synthesis-2, first sentence.
    private const string RunThemeSentence =
        "One study evaluates an agentic framework experimentally in simulated enterprise development environments, reporting measurable gains in productivity, defect reduction, release cycle, and security compliance compared with conventional AI-assisted pipelines [1].";

    // extraction.json, [1] finding 3, with its verified quote.
    private static ReviewEvidence RunEvidence() => new()
    {
        Sources = new[]
        {
            new EvidenceSource
            {
                Reference = 1, ReferenceLine = "Agentic AI Frameworks for Autonomous Enterprise Software Development Workflows.",
                Findings = new[] { new EvidenceFinding("F1.3", "38% productivity, 41% defect reduction, 47% release cycle, 35% compliance gains",
                    "The results show that the productivity gains are 38%, defect reduction 41%, release cycle gain 47%, and security compliance gain 35% compared to conventional AI-assisted pipelines.") },
            },
            new EvidenceSource { Reference = 4, ReferenceLine = "Agentic AI frameworks for autonomous automotive software." },
        },
        BaseFacts = "7 studies: 4 not empirical, 2 quantitative nonrandomized, 1 qualitative.",
    };

    private static readonly string[] RunThemes =
    {
        "Secure orchestration and governance form a foundational layer for safety in agentic frameworks, with [1] describing secure orchestration pipelines and enterprise governance agents for trusted software delivery and [5] detailing a control plane providing authentication, PII masking, and observability. Agentic AI systems utilizing machine learning and reinforcement learning allow agents to adjust to changes in the work environment [2], and multi-agent reinforcement learning-based behavioral simulation converges to safe policies in complex conflict situations [4]. Reasoning and validation loops further support fault tolerance: [4] identifies LLM-based scenario generation, ReAct-style loops, and RAG-based hardware-in-the-loop automation, while [1] highlights retrieval-augmented generation and reinforcement learning for adapting software within secure pipelines.",
        RunThemeSentence,
    };

    // prisma-report.json, DiscussionItem, first two paragraphs.
    private const string RunRetold =
        "The two themes together indicate that safety and fault tolerance in agentic AI frameworks rest on layered mechanisms spanning secure orchestration, adaptive autonomy, and reasoning loops, and that these are assessed through a mix of controlled simulation, aggregated hardware-in-the-loop synthesis, and long-term real-world deployment. Secure orchestration and governance appear as a foundational layer, with enterprise governance agents and control planes providing authentication and observability [1, 5]. Machine learning and reinforcement learning are used in systems that allow agents to adjust to changes in the work environment [2] or converge to safe policies in conflict situations [4]. Reasoning and validation loops, including LLM-based scenario generation and hardware-in-the-loop automation, add further fault-tolerance capacity [4]. Evaluation evidence includes simulated productivity metrics [1], hardware-in-the-loop fault coverage [4], and extended real-world deployment [5].";

    private const string RunAgreement =
        "Simulated gains in productivity and compliance [1] sit alongside hardware-in-the-loop reductions in test authoring effort [4]. The absence of conflict between these results points to consistent directional benefits across evaluation settings.";

    [Fact]
    public void TheRunsConclusionMayNameHowTheEvidenceWasStudied()
    {
        // Issue #561: the conclusion takes no citation with research questions, and must still be able to say "experiments".
        Assert.Null(ReviewWriter.UncitedAttribution(RunConclusion, RunEvidence().BaseFacts, designsAllowed: true));
        string[] answers = { "Secure orchestration and governance agents provide safety [1].", "Evaluation is experimental and simulated [1]." };
        const string known = "Secure orchestration and governance agents provide safety [1]. Evaluation is experimental and simulated [1].";
        string? problem = ReviewWriter.ClosingProblem("The base rests on simulations [1].", "Teams should log what agents do.", "Field trials of the mechanisms are missing.",
            RunConclusion, known, 7, givenAnswers: answers, ownFacts: RunEvidence().BaseFacts);
        Assert.Null(problem);
        // A finding in an uncited sentence is still sent back, in the conclusion as elsewhere.
        Assert.Contains("has no citation", ReviewWriter.ClosingProblem("a [1].", "b", "c", "Studies report that oversight works.", known, 7, givenAnswers: answers, ownFacts: "7 studies."));
    }

    [Fact]
    public void AResultWithoutTheFiguresItsVerifiedFindingGivesIsSentBack()
    {
        string? problem = ReviewWriter.UnquantifiedFindingProblem(RunThemeSentence, RunEvidence());
        Assert.NotNull(problem);
        Assert.Contains("reports a result of [1] without its figures", problem);
        Assert.Contains("productivity gains are 38%", problem);

        Assert.Null(ReviewWriter.UnquantifiedFindingProblem("One study reports a 38% productivity gain and a 41% defect reduction [1].", RunEvidence()));
        Assert.Null(ReviewWriter.UnquantifiedFindingProblem("One review reports measurable gains in coverage [4].", RunEvidence())); // [4] has no figure on file
    }

    [Fact]
    public void ADiscussionThatRetellsTheThemesOrReadsAgreementIntoThemIsSentBack()
    {
        Assert.Equal(1, ReviewWriter.RetoldParagraph(RunRetold + "\n\n" + RunAgreement, RunThemes)?.Paragraph);
        Assert.Contains("discussion paragraph 1 retells the results", ReviewWriter.DiscussionReadingProblem(RunRetold, RunThemes, RunEvidence()));
        Assert.Contains("reads agreement or consistency into the results", ReviewWriter.DiscussionReadingProblem(RunAgreement, RunThemes, RunEvidence()));
        Assert.Null(ReviewWriter.DiscussionReadingProblem(
            "Orchestration and learning address different failure points, so a framework needs both. Neither has been tested in the field at scale.", RunThemes, RunEvidence()));
    }

    [Fact]
    public void AnUncitedClaimOfAgreementIsLeftOutInCodeAndNoCitationIsLost()
    {
        string text = ReviewWriter.WithoutUncitedAgreement("Opening reading.\n\n" + RunAgreement, out string? note);
        Assert.Equal("Opening reading.\n\nSimulated gains in productivity and compliance [1] sit alongside hardware-in-the-loop reductions in test authoring effort [4].", text);
        Assert.Contains("absence of conflict", note);

        const string cited = "Both studies report the same 30% gain, so the results agree [1, 4].";
        Assert.Equal(cited, ReviewWriter.WithoutUncitedAgreement(cited, out string? none));
        Assert.Null(none);
    }

    [Fact]
    public async Task TheDiscussionAndThemePromptsAskForFiguresAndAReadingNotARetelling()
    {
        var model = new FakeChatService().Returns("""{"answers":["Safety rests on orchestration [1]."],"text":"Orchestration and learning address different failure points [1]."}""");
        var sections = new List<SynthesisSection> { new() { Field = "synthesis-1", ThemeId = "T1", Heading = "Evaluation", Text = RunThemeSentence } };
        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(model, WritingProfile.Systematic, "agentic AI", "Map safety", sections, RunEvidence(), 7, null,
            new[] { "Which mechanisms are used?" }, null);
        Assert.Equal("Orchestration and learning address different failure points [1].", result.Text);
        string prompt = model.Prompts.Single();
        Assert.Contains(ReviewWriter.ReadingNotRetellingRule(WritingProfile.Systematic), prompt);
        Assert.Contains(ReviewWriter.VerifiedFigureRule(WritingProfile.Systematic), prompt);
        Assert.Contains("gives those figures, not \"gains\" or \"improvements\"", prompt);
    }
}

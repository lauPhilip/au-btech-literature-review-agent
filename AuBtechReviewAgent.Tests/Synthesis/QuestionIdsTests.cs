using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #461: the multivocal review numbers its questions RQ1, RQ1.1, RQ1.2, RQ2, so the shared writing steps take the
/// ids from its profile, name the closing parts as its guidelines do, and the shared paper has a slot for a conclusion
/// apart from the summary. The systematic review's ids, parts and paper stay as they were.
/// </summary>
public class QuestionIdsTests
{
    private static readonly string[] Questions = { "Which practices?", "Which tools?" };
    private static readonly WritingProfile Nested = WritingProfile.Systematic with { QuestionIds = new[] { "RQ1", "RQ1.1" } };

    [Fact]
    public void AProfileWithoutIdsNumbersTheQuestionsAsBefore()
    {
        Assert.Equal("RQ2", ReviewWriter.QuestionId(1, WritingProfile.Systematic));
        Assert.Equal(ReviewWriter.AimBlock("Goal", Questions), ReviewWriter.AimBlock("Goal", Questions, WritingProfile.Systematic));
        Assert.Contains("RQ2: Which tools?", ReviewWriter.AimBlock("Goal", Questions));
        Assert.Equal("RQ1: a\nRQ2: b", ReviewWriter.AnswerLines(new[] { "a", "b" }));
    }

    [Fact]
    public void AProfileWithIdsGivesThemInEveryPlaceTheQuestionsAreNamed()
    {
        Assert.Equal("RQ1.1", ReviewWriter.QuestionId(1, Nested));
        Assert.Equal("RQ3", ReviewWriter.QuestionId(2, Nested)); // past the given ids, the default
        Assert.Contains("RQ1.1: Which tools?", ReviewWriter.AimBlock("Goal", Questions, Nested));
        Assert.Equal("RQ1: a\nRQ1.1: b", ReviewWriter.AnswerLines(new[] { "a", "b" }, Nested));
        Assert.Equal("The review set out to answer two research questions: (RQ1) Which practices?; (RQ1.1) Which tools?", ReviewWriter.ObjectivesFromQuestions(Questions, Nested));
    }

    [Fact]
    public void TheSystematicReviewNamesItsClosingPartsByPrisma()
    {
        Assert.Equal("the limitations of the evidence base (PRISMA 2020 item 23b)", WritingProfile.Systematic.LimitationsPart);
        Assert.Equal("the implications for practice and policy (item 23d)", WritingProfile.Systematic.ImplicationsPart);
        Assert.Equal("the directions for future research (item 23d)", WritingProfile.Systematic.FutureResearchPart);
    }

    [Fact]
    public void AConclusionApartFromTheSummaryFollowsIt()
    {
        var paper = new ReviewPaper { Title = "T", Summary = new PaperSection("Summary for practitioners", "Do this."), Conclusion = new PaperSection("Conclusion", "It works.") };
        string tex = PaperLatex.Build(paper);
        Assert.True(tex.IndexOf(@"\section{Conclusion}") > tex.IndexOf(@"\section{Summary for practitioners}"));
        Assert.True(tex.IndexOf(@"\section{Conclusion}") < tex.IndexOf(@"\section{Declarations}"));
        Assert.DoesNotContain(@"\section{Conclusion}", PaperLatex.Build(new ReviewPaper { Title = "T" }));
    }
}

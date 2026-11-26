using System.Collections.Generic;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Secondary studies (reviews of other studies) are their own class, decided in code from the study type and the title,
/// so Table A.2, the methods and the answers to the research questions can say that they add no new evidence.
/// </summary>
public class SecondaryStudyTests
{
    [Theory]
    [InlineData("systematic literature review", "Agents in software engineering", "systematic review")]
    [InlineData("meta-analysis of 31 trials", "", "meta-analysis")]
    [InlineData("scoping review", "", "scoping review")]
    [InlineData("literature review", "", "literature review")]
    [InlineData("position paper", "A systematic mapping study of LLM testing", "systematic review")]
    [InlineData("overview", "A Survey on Large Language Model based Autonomous Agents", "survey")]
    [InlineData("overview", "A comprehensive survey of retrieval-augmented generation", "survey")]
    public void Reviews_are_secondary(string studyType, string title, string expected) =>
        Assert.Equal(expected, SecondaryStudy.KindOf(studyType, title));

    [Theory]
    [InlineData("survey", "Developers' use of AI assistants")]
    [InlineData("questionnaire survey", "A survey of 200 practitioners on code review")]
    [InlineData("controlled experiment", "Prompt engineering for test generation")]
    [InlineData("case study", "Peer review in open source")]
    public void Primary_studies_are_not(string studyType, string title) =>
        Assert.Null(SecondaryStudy.KindOf(studyType, title));

    [Fact]
    public void A_failed_extraction_is_not_judged() =>
        Assert.Null(SecondaryStudy.KindOf(new StudyExtraction { StudyType = "systematic review", Error = "failed" }));

    [Fact]
    public void The_methods_sentence_names_each_secondary_study()
    {
        var secondary = SecondaryStudy.In(new[]
        {
            new StudyExtraction { ReferenceNumber = 4, StudyType = "meta-analysis" },
            new StudyExtraction { ReferenceNumber = 2, StudyType = "systematic review" },
            new StudyExtraction { ReferenceNumber = 3, StudyType = "case study" },
        });
        Assert.Equal(new[] { 2, 4 }, secondary.Keys);
        string text = SecondaryStudy.MethodsSentence(secondary);
        Assert.StartsWith("Two included studies are secondary studies, reviews of other studies (a systematic review [2], a meta-analysis [4]).", text);
        Assert.Contains("labelled as secondary in Table A.2", text);
        Assert.Equal("", SecondaryStudy.MethodsSentence(new SortedDictionary<int, string>()));
    }

    [Fact]
    public void An_answer_that_rests_only_on_a_review_is_marked()
    {
        var secondary = new Dictionary<int, string> { [2] = "systematic review" };
        Assert.Equal("This answer rests only on a systematic review [2], a secondary study that summarises other studies; no primary study included in this review supports it.",
            SecondaryStudy.AnswerNote("Agents fail mostly on context [2].", secondary));
        Assert.StartsWith("This answer also cites a systematic review [2]",
            SecondaryStudy.AnswerNote("Agents fail on context [2] and on tools [5].", secondary));
        Assert.Null(SecondaryStudy.AnswerNote("Agents fail on tools [5].", secondary));
        Assert.Null(SecondaryStudy.AnswerNote("Agents fail on tools [2].", null));
    }
}

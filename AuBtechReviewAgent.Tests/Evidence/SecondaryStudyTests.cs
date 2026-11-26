using System.Collections.Generic;
using System.Linq;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Secondary studies (reviews of other studies) are their own class, decided in code from the paper's own words (its
/// title, or a verified quote of its method), so Table A.2, the methods and the answers to the research questions can say
/// that they add no new evidence. The model's study type alone never decides it (AGENTS.md rule 1).
/// </summary>
public class SecondaryStudyTests
{
    private static StudyExtraction Study(string title, string studyType = "", string methodQuote = "", bool verified = true, int number = 1) => new()
    {
        ReferenceNumber = number,
        Title = title,
        StudyType = studyType,
        Method = new EvidencedValue { Value = "method", Quote = methodQuote, QuoteVerified = verified },
    };

    [Theory]
    [InlineData("A systematic mapping study of LLM testing", "systematic review")]
    [InlineData("A Survey on Large Language Model based Autonomous Agents", "survey")]
    [InlineData("A comprehensive survey of retrieval-augmented generation", "survey")]
    [InlineData("Agent memory: a scoping review", "scoping review")]
    [InlineData("Prompting for code: a meta-analysis", "meta-analysis")]
    public void A_title_that_names_a_review_makes_it_secondary(string title, string expected)
    {
        var found = SecondaryStudy.Classify(Study(title, studyType: "position paper"));
        Assert.Equal(expected, found?.Kind);
        Assert.Equal("title", found?.Basis);
        Assert.Equal(title, found?.Text);
    }

    [Theory]
    [InlineData("Developers' use of AI assistants")]
    [InlineData("A survey of 200 practitioners on code review")]
    [InlineData("Peer review in open source")]
    public void A_primary_title_is_not_secondary(string title) => Assert.Null(SecondaryStudy.KindInTitle(title));

    [Fact]
    public void A_verified_method_quote_counts_when_the_model_also_calls_it_a_review()
    {
        const string quote = "We conducted a systematic literature review of 45 papers.";
        var found = SecondaryStudy.Classify(Study("Agents in software engineering", "systematic literature review", quote));
        Assert.Equal(("systematic review", "method quote", quote), found);
    }

    [Fact]
    public void The_study_type_alone_never_makes_a_study_secondary()
    {
        // No quote names a review: the model's label is not evidence from the paper.
        Assert.Null(SecondaryStudy.Classify(Study("Agents in software engineering", "systematic review", "We ran 12 interviews.")));
        // The quote names a review but was not found in the paper's text.
        Assert.Null(SecondaryStudy.Classify(Study("Agents in software engineering", "systematic review", "We conducted a systematic review.", verified: false)));
        // The quote mentions an earlier review, but the model called the study primary.
        Assert.Null(SecondaryStudy.Classify(Study("Agents in software engineering", "interview study", "Following a systematic review, we interviewed 12 engineers.")));
    }

    [Fact]
    public void A_failed_extraction_is_not_judged() =>
        Assert.Null(SecondaryStudy.KindOf(new StudyExtraction { Title = "A systematic review of agents", Error = "failed" }));

    [Fact]
    public void The_methods_sentence_names_each_secondary_study()
    {
        var secondary = SecondaryStudy.In(new[]
        {
            Study("Prompting for code: a meta-analysis", number: 4),
            Study("Agents", "systematic review", "We conducted a systematic review of 30 studies.", number: 2),
            Study("Agents in practice", "case study", "We studied one firm.", number: 3),
        });
        Assert.Equal(new[] { 2, 4 }, secondary.Keys);
        string text = SecondaryStudy.MethodsSentence(secondary);
        Assert.StartsWith("Two included studies are secondary studies, reviews of other studies (a systematic review [2], a meta-analysis [4]), decided in code from each title or from a quote of the method found word for word in the study's text.", text);
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

    [Fact]
    public void A_study_whose_extracted_type_is_a_review_counts_as_a_review_in_the_article_info()
    {
        var all = new[]
        {
            Study("A framework", "system description", number: 1),
            Study("Guardrails for agents", "case study", number: 2),
            Study("Agent safety on Zenodo", "systematic literature review", number: 3),
        };
        Assert.Equal(1, SecondaryStudy.ReviewCount(all));
        // A bare "survey" names questionnaires too, so it is not counted; a title that names a review is.
        Assert.Equal(1, SecondaryStudy.ReviewCount(all.Append(Study("Questionnaire on tools", "survey", number: 4))));
        Assert.Equal(2, SecondaryStudy.ReviewCount(all.Append(Study("Agent memory: a scoping review", "position paper", number: 4))));
        Assert.Equal(0, SecondaryStudy.ReviewCount(null));
    }
}

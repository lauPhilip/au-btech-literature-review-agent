using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The certainty of the evidence behind each answer, rated in code from hand-built evidence: where the rating starts,
/// each downgrade with its reason and studies, the very-low floor, unanswered questions and the sensitivity sentence.
/// </summary>
public class EvidenceCertaintyTests
{
    private static CertaintySource S(int r, bool empirical = true, bool abstractOnly = false, bool fails = false, string[]? questions = null) =>
        new(r, empirical, abstractOnly, fails, questions);

    private static CertaintyQuestion Q(string answer, int[] cited, CertaintyTheme[]? themes = null, string? notRated = null, int[]? contested = null) =>
        new(1, "RQ1", "How do agent loops recover?", answer, cited, themes ?? Array.Empty<CertaintyTheme>(), notRated, contested);

    private static CertaintyAssessment Rate(CertaintyQuestion q, params CertaintySource[] sources) =>
        EvidenceCertainty.Assess(q, sources, CertaintyRule.Default, CertaintyWords.Systematic);

    private static CertaintyDomain Domain(CertaintyAssessment a, string name) => Assert.Single(a.Domains, d => d.Domain == name);

    [Fact]
    public void EmpiricalFullTextEvidenceWithoutConcernsIsHigh()
    {
        var a = Rate(Q("Loops recover through supervisor checks [1, 2]. They also retry [2].", new[] { 1, 2 }), S(1), S(2));

        Assert.Equal("high", a.Start);
        Assert.Equal("high", a.Overall);
        Assert.Equal("Loops recover through supervisor checks [1, 2].", a.AnswerInBrief);
        Assert.Equal("(high certainty: two studies)", a.Phrase);
        Assert.DoesNotContain(a.Domains, d => d.Concern == EvidenceCertainty.Serious);
        Assert.Equal(EvidenceCertainty.NotAssessed, Domain(a, EvidenceCertainty.Coherence).Concern);
        Assert.Equal(EvidenceCertainty.NotAssessed, Domain(a, EvidenceCertainty.Relevance).Concern);
        Assert.Equal("No study behind the answer was read from the abstract only or fails more than half of its MMAT criteria, so leaving such studies out changes nothing.", a.SensitivitySentence);
    }

    [Fact]
    public void TheStartFollowsHowManyStudiesAreEmpirical()
    {
        var moderate = Rate(Q("A [1, 2].", new[] { 1, 2 }), S(1), S(2, empirical: false));
        var low = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }), S(1), S(2, empirical: false), S(3, empirical: false));

        Assert.Equal("moderate", moderate.Start);
        Assert.Equal("One of the two studies is empirical; [2] is not (position papers, system descriptions or reviews).", moderate.StartReason);
        Assert.Equal("(moderate certainty: two studies, one not empirical)", moderate.Phrase);
        Assert.Equal("low", low.Start);
    }

    [Fact]
    public void MoreThanHalfFailingTheAppraisalLowersTheRatingWithItsStudies()
    {
        var a = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }), S(1, fails: true), S(2, fails: true), S(3));
        var limitations = Domain(a, EvidenceCertainty.Limitations);

        Assert.Equal(EvidenceCertainty.Serious, limitations.Concern);
        Assert.Equal(new[] { 1, 2 }, limitations.Sources);
        Assert.Equal("Two of the three studies ([1], [2]) fail more than half of their MMAT criteria, more than half of them.", limitations.Reason);
        Assert.Equal("moderate", a.Overall);
        Assert.Equal("(moderate certainty: three studies, two fail most MMAT criteria)", a.Phrase);

        // Exactly half is not more than half.
        var half = Rate(Q("A [1, 2].", new[] { 1, 2 }), S(1, fails: true), S(2));
        Assert.Equal(EvidenceCertainty.NoConcern, Domain(half, EvidenceCertainty.Limitations).Concern);
        Assert.Equal("high", half.Overall);
    }

    [Fact]
    public void ReadingFromTheAbstractOnlyIsItsOwnDowngradeAndTheSensitivityCheckCountsWhatRemains()
    {
        var a = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }), S(1, abstractOnly: true), S(2, abstractOnly: true), S(3));

        Assert.Equal(EvidenceCertainty.Serious, Domain(a, EvidenceCertainty.AbstractOnly).Concern);
        Assert.Equal("(moderate certainty: three studies, two read from the abstract only)", a.Phrase);
        Assert.Equal("Without the two studies read from the abstract only ([1], [2]), the answer still rests on one study; none fails more than half of its MMAT criteria.", a.SensitivitySentence);
        Assert.Equal(new[] { 3 }, a.Sensitivity[0].Remaining);
    }

    [Fact]
    public void OneStudyIsTooLittleDataAndLeavingItOutLeavesNothing()
    {
        var a = Rate(Q("A [4].", new[] { 4 }), S(4, abstractOnly: true));

        var adequacy = Domain(a, EvidenceCertainty.Adequacy);
        Assert.Equal(EvidenceCertainty.Serious, adequacy.Concern);
        Assert.Equal("The answer rests on one study ([4]), fewer than 2.", adequacy.Reason);
        Assert.Equal("low", a.Overall); // high, less one for the abstract and one for the single study
        Assert.Equal("(low certainty: one study, one read from the abstract only)", a.Phrase);
        Assert.Equal("Without the one study read from the abstract only ([4]), the answer would rest on no study; none fails more than half of its MMAT criteria.", a.SensitivitySentence);
    }

    [Fact]
    public void RelevanceIsRatedOnlyWhereTheCodesNameTheirQuestions()
    {
        var indirect = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }), S(1, questions: new[] { "RQ2" }), S(2, questions: new[] { "RQ2" }), S(3, questions: new[] { "RQ1" }));
        var relevance = Domain(indirect, EvidenceCertainty.Relevance);
        Assert.Equal(EvidenceCertainty.Serious, relevance.Concern);
        Assert.Equal("Two of the three studies ([1], [2]) have no code that bears on RQ1, more than half of them.", relevance.Reason);
        Assert.Contains("two with no code on the question", indirect.Phrase);

        var untagged = Rate(Q("A [1, 2].", new[] { 1, 2 }), S(1), S(2));
        Assert.Equal(EvidenceCertainty.NotAssessed, Domain(untagged, EvidenceCertainty.Relevance).Concern);
    }

    [Fact]
    public void CoherenceComesFromTheSecondCodingAndIsNotAssessedWithoutOne()
    {
        var none = Rate(Q("A [1, 2].", new[] { 1, 2 }), S(1), S(2));
        Assert.Equal("The run has no second, independent coding of the studies' quotes, so coherence is not rated and lowers nothing.", Domain(none, EvidenceCertainty.Coherence).Reason);

        var agreed = Rate(Q("A [1, 2].", new[] { 1, 2 }, contested: Array.Empty<int>()), S(1), S(2));
        Assert.Equal(EvidenceCertainty.NoConcern, Domain(agreed, EvidenceCertainty.Coherence).Concern);

        // Study 7 is not behind the answer, so its disagreement does not count.
        var mixed = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }, contested: new[] { 1, 2, 7 }), S(1), S(2), S(3));
        var coherence = Domain(mixed, EvidenceCertainty.Coherence);
        Assert.Equal(EvidenceCertainty.Serious, coherence.Concern);
        Assert.Equal(new[] { 1, 2 }, coherence.Sources);
        Assert.Equal("Two of the three studies ([1], [2]) were placed in a theme of RQ1 by only one of two independent codings of their quotes, more than half of them.", coherence.Reason);
        Assert.Equal("moderate", mixed.Overall);
        Assert.Contains("two placed by one coding only", mixed.Phrase);
    }

    [Fact]
    public void TheRatingNeverFallsBelowVeryLow()
    {
        // Low start (one of three empirical), then limitations, abstract only and relevance.
        var a = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }),
            S(1, fails: true, abstractOnly: true, questions: new[] { "RQ2" }),
            S(2, empirical: false, fails: true, abstractOnly: true, questions: new[] { "RQ2" }),
            S(3, empirical: false, questions: new[] { "RQ2" }));

        Assert.Equal("low", a.Start);
        Assert.Equal(3, a.Domains.Count(d => d.Concern == EvidenceCertainty.Serious));
        Assert.Equal("very low", a.Overall);
    }

    [Fact]
    public void AnUnansweredQuestionIsNotRatedAndSaysWhy()
    {
        var a = Rate(Q("None of the findings bears on this question.", Array.Empty<int>(), notRated: "No evidence bears on this question."));

        Assert.Null(a.Overall);
        Assert.Null(a.Start);
        Assert.Equal("No evidence bears on this question.", a.NotRated);
        Assert.Equal("(certainty not rated: no evidence bears on this question)", a.Phrase);
        Assert.Empty(a.Domains);

        var uncited = Rate(Q("The results do not say.", Array.Empty<int>()));
        Assert.Equal("The answer cites no study and no theme is mapped to the question, so there is no evidence to rate.", uncited.NotRated);
    }

    [Fact]
    public void TheEvidenceIncludesTheThemesOfTheQuestionAndTheCheckNamesAThemeLeftWithout()
    {
        var themes = new[] { new CertaintyTheme("T1", "Recovery", new[] { 2, 3 }), new CertaintyTheme("T2", "Oversight", new[] { 4 }) };
        var a = Rate(Q("A [1].", new[] { 1 }, themes), S(1), S(2), S(3), S(4, abstractOnly: true));

        Assert.Equal(new[] { 1, 2, 3, 4 }, a.Sources);
        Assert.Equal(new[] { "T2 (Oversight)" }, a.Sensitivity[0].ThemesWithout);
        Assert.Equal("Without the one study read from the abstract only ([4]), the answer still rests on three studies, and theme T2 (Oversight) would keep none; none fails more than half of its MMAT criteria.", a.SensitivitySentence);
    }

    [Fact]
    public void TheSameEvidenceGivesTheSameRatingWhateverTheOrder()
    {
        var q = new[] { Q("A [3, 1].", new[] { 3, 1 }), Q("B [2].", new[] { 2 }) with { Number = 2, Id = "RQ2" } };
        var sources = new[] { S(1, abstractOnly: true), S(2, fails: true), S(3, empirical: false) };
        string first = JsonSerializer.Serialize(EvidenceCertainty.Assess(q, sources));
        string again = JsonSerializer.Serialize(EvidenceCertainty.Assess(q.Reverse(), sources.Reverse().ToArray()));

        Assert.Equal(first, again);
    }

    [Fact]
    public void TheSummaryCountsTheLevelsAndNamesAnswersThatDoNotHoldWithoutTheWeakestStudies()
    {
        var held = Rate(Q("A [1, 2, 3].", new[] { 1, 2, 3 }), S(1), S(2), S(3, abstractOnly: true));
        var fragile = Rate(Q("B [4, 5].", new[] { 4, 5 }) with { Number = 2, Id = "RQ2" }, S(4, fails: true), S(5, abstractOnly: true));
        var empty = Rate(Q("None.", Array.Empty<int>(), notRated: "No evidence.") with { Number = 3, Id = "RQ3" });

        Assert.Equal("Of the three answers to the research questions, two are of high certainty and one is not rated, because no evidence bears on it. " +
                     "The answer to RQ2 would rest on fewer than two studies without the studies read from the abstract only, or those that fail more than half of their MMAT criteria, so it does not hold without them.",
            EvidenceCertainty.Summary(new[] { held, fragile, empty }));
        Assert.Equal("The answer to the research question is of high certainty. No rated answer comes to rest on fewer than two studies when the studies read from the abstract only, or those that fail more than half of their MMAT criteria, are left out.",
            EvidenceCertainty.Summary(new[] { held }));
        Assert.Equal("", EvidenceCertainty.Summary(Array.Empty<CertaintyAssessment>()));
    }

    [Fact]
    public void TheMethodIsWrittenFromTheRuleAndSaysItIsAnApproximation()
    {
        string method = EvidenceCertainty.Method();
        Assert.Contains("GRADE-CERQual (Lewin et al., 2018) and GRADE (Guyatt et al., 2008)", method);
        Assert.Contains("It is an automated approximation, not a formal GRADE or GRADE-CERQual assessment made by people.", method);
        Assert.Contains("fewer than 2 studies, so a single study is never enough (adequacy of data)", method);
        Assert.Contains("certainty.json", method);
        Assert.Contains("fewer than 3 studies", EvidenceCertainty.Method(new CertaintyRule { FewestSources = 3 }));
    }
}

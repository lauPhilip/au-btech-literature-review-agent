using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Alternatives in the inclusion criterion (issue #670). In run 8942b961 the screener split "Studies that describe or
/// evaluate an agent framework or orchestration loop, with some form of evaluation" into parts that included
/// "or orchestration loop", and code excluded a record it had kept because no quote showed that alternative.
/// </summary>
public class CriterionAlternativesTests
{
    private const string Inclusion = "Studies that describe or evaluate an agent framework or orchestration loop, with some form of evaluation";

    private static readonly AcademicPaper Paper = new("S2_x", "A multi-agent framework for secure code generation",
        "We describe a multi-agent framework with specialised AI agents and a continuous security gate. An evaluation on 40 repositories measures detection rates and operational effectiveness.",
        "Published: 2025", new() { "A. Author" }, "Journal");

    private static CriterionCheck Part(string part, string met, string? quote = null) => new() { Part = part, Met = met, Quote = quote };

    private static ScreeningAnswer Included(params CriterionCheck[] parts) =>
        new() { Decision = "Included", Reasoning = "r", BriefSummary = "s", CriteriaParts = parts.ToList() };

    private static CriterionCheck Framework => Part("describe or evaluate an agent framework", "yes", "We describe a multi-agent framework with specialised AI agents");
    private static CriterionCheck Evaluation => Part("with some form of evaluation", "yes", "An evaluation on 40 repositories measures detection rates");

    [Fact]
    public void AnUnshownAlternativeDoesNotExcludeTheRecord()
    {
        var answer = CriterionParts.Apply(Included(Framework, Part("or orchestration loop", "unclear"), Evaluation), Paper, Inclusion);

        Assert.Equal("Included", answer.Decision);
        Assert.Equal("r", answer.Reasoning);
        var parts = answer.CriteriaParts!;
        Assert.Equal("Checked as an alternative to \"or orchestration loop\"; one alternative is enough.", parts[0].Note);
        Assert.Equal("Checked as an alternative to \"describe or evaluate an agent framework\"; one alternative is enough.", parts[1].Note);
        Assert.Null(parts[2].Note);
    }

    [Fact]
    public void AWholeRequirementThatIsNotShownStillExcludesTheRecord()
    {
        var answer = CriterionParts.Apply(Included(Framework, Part("or orchestration loop", "yes", "orchestration loop"), Part("with some form of evaluation", "yes", "suggesting a focus on evaluation")), Paper, Inclusion);

        Assert.Equal("Excluded", answer.Decision);
        Assert.Equal(ExclusionReasons.TooLittleInformation, answer.ExclusionReason);
        Assert.Equal("(Excluded in code: the record does not show this part of the inclusion criterion with a quote from its title or abstract: \"with some form of evaluation\". The screener had decided Included.) r", answer.Reasoning);
        Assert.Equal("Marked unclear in code: the quote was not found in the title or abstract. Checked as an alternative to \"describe or evaluate an agent framework\"; one alternative is enough.",
            answer.CriteriaParts![1].Note);
    }

    [Fact]
    public void ARequirementWhoseAlternativesAreAllUnshownIsNamedWithTheirOr()
    {
        var answer = CriterionParts.Apply(Included(Part("describe or evaluate an agent framework", "no"), Part("or orchestration loop", "no"), Evaluation), Paper, Inclusion);

        Assert.Equal("Excluded", answer.Decision);
        Assert.Equal(ExclusionReasons.InclusionNotMet, answer.ExclusionReason);
        Assert.StartsWith("(Excluded in code: the record does not show this part of the inclusion criterion with a quote from its title or abstract: \"describe or evaluate an agent framework\" or \"or orchestration loop\". ", answer.Reasoning);
    }

    [Fact]
    public void AnAndOrPartIsAnAlternativeButAnAndPartIsNot()
    {
        const string criterion = "Studies of agent frameworks and/or orchestration loops and with an evaluation";
        var andOr = CriterionParts.Requirements(new[] { Part("agent frameworks", "yes"), Part("And/Or orchestration loops", "no") }, criterion);
        Assert.Single(andOr);

        var and = CriterionParts.Requirements(new[] { Part("agent frameworks", "yes"), Part("and with an evaluation", "no") }, criterion);
        Assert.Equal(2, and.Count);
        Assert.Equal("Excluded", CriterionParts.Apply(Included(Part("agent frameworks", "yes", "We describe a multi-agent framework"), Part("and with an evaluation", "unclear")), Paper, criterion).Decision);
    }

    [Fact]
    public void TheTwoSidesOfOneOrPhraseAreAlternatives()
    {
        var answer = CriterionParts.Apply(Included(Part("agent framework", "yes", "We describe a multi-agent framework"), Evaluation, Part("Orchestration loop", "no")), Paper, Inclusion);

        Assert.Equal("Included", answer.Decision);
        Assert.Equal("Checked as an alternative to \"agent framework\"; one alternative is enough.", answer.CriteriaParts![2].Note);
        Assert.Null(answer.CriteriaParts[1].Note);

        // Parts that only share words with the criterion, not one "X or Y" phrase, stay separate requirements.
        Assert.Equal(2, CriterionParts.Requirements(new[] { Part("evaluate an agent framework", "yes"), Part("with some form of evaluation", "yes") }, Inclusion).Count);
        Assert.Equal(2, CriterionParts.Requirements(new[] { Part("framework", "yes"), Part("loop", "yes") }, Inclusion).Count);
    }

    [Fact]
    public void AnIncludedAnswerMaySayNoToOneAlternative()
    {
        Assert.Null(CriterionParts.Problem(Included(Framework, Part("or orchestration loop", "no"), Evaluation), Inclusion));
        Assert.Null(CriterionParts.Problem(Included(Part("agent framework", "no"), Part("orchestration loop", "yes"), Evaluation), Inclusion));
        Assert.Equal("A record that does not meet a part of the inclusion thresholds cannot be Included.",
            CriterionParts.Problem(Included(Part("describe or evaluate an agent framework", "no"), Part("or orchestration loop", "no"), Evaluation), Inclusion));
        Assert.Equal("A record that does not meet a part of the inclusion thresholds cannot be Included.",
            CriterionParts.Problem(Included(Framework, Part("with some form of evaluation", "no")), Inclusion));
    }
}

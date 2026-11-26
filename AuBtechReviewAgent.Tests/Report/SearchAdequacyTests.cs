using System.Linq;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The search-adequacy guard: a run with a small per-source cap or few records identified is a pilot search, says so in
/// its limitations and abstract facts, and is not called a systematic literature review in its title or kicker; and agreement
/// between two prompts of one model is reported with raw agreement, PABAK and n.
/// </summary>
public class SearchAdequacyTests
{
    private static ReviewState Run(int cap, int identified) => new() { MaxResultsPerSource = cap, Stats = new ReviewStats { TotalIdentified = identified } };

    [Fact]
    public void A_small_cap_or_few_records_make_a_pilot()
    {
        Assert.True(SearchAdequacy.IsPilot(Run(20, 400)));
        Assert.True(SearchAdequacy.IsPilot(Run(200, 60)));
        Assert.False(SearchAdequacy.IsPilot(Run(50, 100)));
        Assert.False(SearchAdequacy.IsPilot(Run(0, 500)));
        Assert.False(SearchAdequacy.IsPilot(null));
    }

    [Fact]
    public void The_limitations_and_the_facts_say_why()
    {
        Assert.Equal("This was a pilot run, not a comprehensive search: the search was capped below the 50 records per source that a full search needs and identified 57 records, so relevant studies are likely to be missing and the findings show what a small sample of the literature says, not the state of the field. ",
            SearchAdequacy.LimitationSentence(Run(20, 57)));
        Assert.Contains("pilot run, not a comprehensive search", SearchAdequacy.AbstractFact(Run(20, 400)));
        Assert.Equal("", SearchAdequacy.LimitationSentence(Run(100, 400)));
        Assert.Equal("", SearchAdequacy.AbstractFact(Run(100, 400)));
    }

    [Theory]
    [InlineData("Agent memory: A Systematic Literature Review", "Agent memory: A Pilot Systematic Search")]
    [InlineData("Agent memory: a systematic review of the evidence", "Agent memory: a pilot systematic search of the evidence")]
    [InlineData("Systematic Mapping Review of agent memory", "Pilot Systematic Search of agent memory")]
    [InlineData("How agents remember", "How agents remember")]
    public void A_pilot_title_does_not_claim_a_systematic_review(string title, string expected) =>
        Assert.Equal(expected, SearchAdequacy.Title(title, Run(20, 400)));

    [Fact]
    public void Titles_and_kickers_of_full_runs_stay_as_they_were()
    {
        Assert.Equal("Agent memory: A Systematic Literature Review", SearchAdequacy.Title("Agent memory: A Systematic Literature Review", Run(100, 400)));
        Assert.Equal("Systematic review", SearchAdequacy.Kind(Run(100, 400)));
        Assert.Equal("Pilot systematic search", SearchAdequacy.Kind(Run(20, 400)));
    }

    [Fact]
    public void Agreement_is_reported_with_raw_agreement_pabak_and_n()
    {
        Assert.Equal("raw agreement 95% (19 of 20); Cohen's kappa = 0.40; PABAK = 0.90; n = 20", Agreement.Figures(19, 20, 0.4));
        Assert.Equal("raw agreement 50% (5 of 10); PABAK = 0.00; n = 10", Agreement.Figures(5, 10, null));
        Assert.Equal(0.9, new ScreeningConfusion(1, 1, 18, 0).Pabak, 3);
        Assert.Equal("Raw agreement was 75% (3 of 4), PABAK = 0.50, n = 4. ", Agreement.Sentence(3, 4));
        Assert.Contains("not inter-rater reliability", Agreement.SameModelSentence("screenings"));
    }

    [Theory]
    [InlineData("Safety and Fault-Tolerance Mechanisms in Agentic AI Frameworks: A Pilot Systematic Literature Review",
        "Safety and Fault-Tolerance Mechanisms in Agentic AI Frameworks: A Pilot Systematic Search")]
    [InlineData("Agent memory: a pilot systematic review", "Agent memory: a pilot systematic search")]
    [InlineData("Agent memory: Pilot Systematic Literature Review", "Agent memory: Pilot Systematic Search")]
    public void A_pilot_in_front_of_the_claim_is_not_said_twice(string title, string expected) =>
        Assert.Equal(expected, SearchAdequacy.Title(title, Run(10, 24)));

    [Fact]
    public void The_checklist_names_the_pilot_line_above_the_title_for_a_pilot_run()
    {
        var row = PrismaChecklist.Rows(new PrismaChecklistFacts { Title = "Agents: A Pilot Systematic Search", IsPilot = true }).First(r => r.Item == "1");
        Assert.Contains("pilot systematic search", row.Reason);
        Assert.DoesNotContain("names the report a systematic review", row.Reason);
        var normal = PrismaChecklist.Rows(new PrismaChecklistFacts { Title = "Agents" }).First(r => r.Item == "1");
        Assert.Contains("names the report a systematic review", normal.Reason);
    }

    [Fact]
    public void The_per_source_cap_is_said_once_in_the_limitations()
    {
        var state = Run(10, 24);
        string paragraph = MethodsSectionWriter.ReviewProcessLimitations(state, "m", "m") + " " + SearchAdequacy.LimitationSentence(state);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(paragraph, "at most 10 records per source").Count);
        Assert.Contains("pilot run, not a comprehensive search", paragraph);
    }
}

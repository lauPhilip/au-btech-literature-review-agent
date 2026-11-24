using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #485: half of a surrogate pair in a form field made string.Normalize throw, so the form answered with a server
/// error. The guard removes lone surrogates before it normalises; a whole pair (an emoji) stays.
/// </summary>
public class LoneSurrogateTests
{
    private const string High = "\uD83D", Low = "\uDE00";

    [Fact]
    public void A_lone_surrogate_is_removed_and_the_rest_is_kept()
    {
        // Built here, not in InlineData, which would turn a lone surrogate into U+FFFD before the test sees it.
        foreach (var typed in new[] { "agent" + High + " loops", Low + "agent loops", "agent loops" + High, "agent " + Low + High + "loops" })
            Assert.Equal("agent loops", ReviewInputGuard.Normalize(typed, singleLine: true));
    }

    [Fact]
    public void A_whole_surrogate_pair_stays()
    {
        Assert.Equal("agents " + High + Low, ReviewInputGuard.Normalize("agents " + High + Low, singleLine: true));
    }

    [Fact]
    public void The_review_form_check_answers_with_a_clean_request_not_an_exception()
    {
        var check = ReviewInputGuard.Check(new ReviewRequest("agent" + High + " loops", "Map " + Low + "loops", "Agent" + High + " architecture", "", 3));
        Assert.Equal("agent loops", check.Request.Query);
        Assert.Equal("Map loops", check.Request.Objective);
        Assert.Equal("Agent architecture", check.Request.Inclusion);
        Assert.DoesNotContain(check.Errors, e => e.Contains("Search query"));
    }

    [Fact]
    public void The_multivocal_plan_and_its_guard_take_a_lone_surrogate_too()
    {
        var plan = MultivocalPlan.Example();
        plan.Topic = "Context" + High + " engineering";
        plan.GreySearchStrings = new() { "context " + Low + "engineering" };

        Assert.Empty(MultivocalPlanGuard.Flags(plan));
        plan.Normalized();
        Assert.Equal("Context engineering", plan.Topic);
        Assert.Equal(new[] { "context engineering" }, plan.GreySearchStrings);
    }
}

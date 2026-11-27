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

    // Found by the fuzz tests (#623): U+FFFE is no character, and string.Normalize throws on it just as on a lone surrogate.
    [Fact]
    public void The_non_character_U_FFFE_is_removed_and_does_not_throw()
    {
        Assert.Equal("agent loops", ReviewInputGuard.Normalize("agent\uFFFE loops", singleLine: true));
        Assert.Equal("agents " + High + Low, ReviewInputGuard.Normalize("agents " + High + Low + "\uFFFE", singleLine: true));
        Assert.Equal("Agent loops", ReviewInputGuard.Check(new ReviewRequest("agent loops", "", "Agent\uFFFE loops", "", 3)).Request.Inclusion);
    }

    [Fact]
    public void The_input_of_fuzz_seed_1977725175_is_cleaned_without_an_exception()
    {
        string input = "\uDE6C.\u2067\u202A\uDB40\uDC4E\uDB40\uDC78>>\u202C\u202E\u2064\u2062.\uDB40\uDC68\uDB40\uDC19system\u0085\uDA3C\uDEB0\u00A0\uDB40\uDC0A\uA64Dinstructions\uD83D\uDE00\u000E\u008C\uDE35\u00E9\u200C\u0089\u000A\uD83D\uDE00\uDB40\uDC53\uDB40\uDC73\uFFFE";

        Assert.DoesNotContain('\uFFFE', ReviewInputGuard.Clean(input, singleLine: false).Text);
    }
}

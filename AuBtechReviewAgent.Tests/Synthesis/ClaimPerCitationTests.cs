using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #248: most citations the live runs lost after the repair were claims that added a number, a qualifier, a
/// comparison or a claim several sources were cited for together. The theme writer is asked for one claim per
/// citation and no counts of its own, in both kinds of review.
/// </summary>
public class ClaimPerCitationTests
{
    [Fact]
    public void TheRuleNamesTheReviewsSourcesAndEveryElementTheCheckCallsPartlySupported()
    {
        string grey = ReviewWriter.ClaimPerCitationRule(MultivocalWriter.Profile(MultivocalPlan.Example("grey")));
        string systematic = ReviewWriter.ClaimPerCitationRule(WritingProfile.Systematic);

        Assert.Contains("cite several sources together only when each of them states the whole claim", grey);
        Assert.Contains("\"most studies\"", systematic);
        foreach (var rule in new[] { grey, systematic })
            foreach (var element in new[] { "one finding per citation", "comparison", "number, count or proportion", "qualifier", "scope", "causal link", "takes no citation" })
                Assert.Contains(element, rule);
    }

    [Fact]
    public async Task TheGreyLiteratureThemeWriterIsAskedForOneClaimPerCitation()
    {
        var model = new FakeChatService().RespondsWith(MultivocalWritingTests.Respond);

        await MultivocalWriter.WritePaperAsync(model, MultivocalPaperTests.Input(), _ => "We keep a running summary of the conversation.", "fake-model", parallelism: 1);

        string prompt = Assert.Single(model.Prompts, p => p.Contains("You are writing one subsection of the Results"));
        Assert.Contains("6. " + ReviewWriter.ClaimPerCitationRule(MultivocalWriter.Profile(MultivocalPaperTests.Input().Planned.Plan)), prompt);
    }
}

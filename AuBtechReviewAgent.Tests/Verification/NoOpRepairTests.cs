using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #569: in run 289a0d3e, citation-audit.json recorded a "rewrite" ("removed [1] from the unsupported first clause")
/// whose After was the same as its Before. A repair that changes nothing is now recorded as not applied, with the reason,
/// and is left out of the count of repaired sentences.
/// </summary>
public class NoOpRepairTests
{
    private const string Partly = """{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"every tool call is logged","reason":"tool calls only"}]}""";
    private const string Sentence = "Agents log every tool call and every prompt [1].";

    [Fact]
    public async Task ARewriteThatGivesBackTheSameSentenceIsNotApplied()
    {
        var chat = new FakeChatService().RespondsWith(p => !p.Contains("You are correcting citations") ? Partly
            : $$"""{"repairs":[{"id":1,"action":"rewrite","sentence":"{{Sentence}}","reason":"removed [1] from the unsupported first clause"}]}""");
        var page = new ReferencedPaper(1, "web1", "Vendor documentation", "", new[] { new DocumentChunk("web1", 1, "In this release every tool call is logged.") });

        var result = await Grounding.CheckAndRepairAsync(chat, new() { ("synthesis-1", Sentence) }, new[] { page },
            new Dictionary<int, IReadOnlyList<string>>(), 1, new GroundingOptions(Parallelism: 1, SecondCheck: false, Repair: true, SenseCheck: false));

        Assert.Equal(Sentence, Assert.Single(result.Fields).Text);
        Assert.All(result.Repairs, r => Assert.Equal("not applied", r.Action));
        var first = result.Repairs.First();
        Assert.Equal(first.Before, first.After);
        Assert.StartsWith("The proposed rewrite left the sentence unchanged; the sentence keeps its verdict. The model's reason was: removed [1]", first.Note);
        Assert.DoesNotContain(result.Repairs, r => r.Action is "rewrite" or "drop_citation" or "delete");
    }
}

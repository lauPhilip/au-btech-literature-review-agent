using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The audit of two real runs: the repair, not knowing a section's role, rewrote a direction for future research into
/// a finding ("AI can consistently achieve test coverage ... improvements of 40-90"). Each flagged item now carries
/// its section's role, and the prompt prefers dropping the citation over turning a proposal into a finding.
/// </summary>
public class RepairRoleTests
{
    private const string Partly = """{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"every tool call is logged","reason":"tool calls only"}]}""";
    private const string Proposal = "Field trials should test whether agents log every prompt [1].";

    [Theory]
    [InlineData("discussionItem", "Discussion: interprets the findings")]
    [InlineData("discussion", "Discussion: interprets the findings")]
    [InlineData("futureResearchItem", "Future research: proposes a study")]
    [InlineData("implicationsItem", "Implications: says what to do")]
    [InlineData("evidenceLimitationsItem", "Limitations: says what limits the evidence")]
    [InlineData("rationaleItem", "Rationale: states the problem and the gap")]
    [InlineData("rationale", "Rationale: states the problem and the gap")]
    [InlineData("researchAnswer2", "Answer to a research question: answers it from the findings")]
    [InlineData("synthesis-3", "Results: reports what the sources found")]
    public void EachSectionHasItsRole(string field, string role) => Assert.Equal(role, CitationRepairer.SectionRole(field));

    [Fact]
    public void APartWithoutAFixedRoleHasNone() => Assert.Null(CitationRepairer.SectionRole("artifact-n2"));

    [Fact]
    public async Task TheRepairOfAProposalCarriesItsRoleAndDropsTheCitationRatherThanRewritingIt()
    {
        // A fake repair that keeps the role when told to, and otherwise narrows the sentence into a finding, as the runs did.
        var chat = new FakeChatService().RespondsWith(p => !p.Contains("You are correcting citations") ? Partly
            : p.Contains("SECTION ROLE: Future research: proposes a study.") && p.Contains("prefer \"drop_citation\"")
                ? """{"repairs":[{"id":1,"action":"drop_citation","reason":"the proposal is the review's own"}]}"""
                : """{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log every tool call [1].","reason":"narrowed"}]}""");
        var page = new ReferencedPaper(1, "web1", "Vendor documentation", "", new[] { new DocumentChunk("web1", 1, "In this release every tool call is logged.") });

        var result = await Grounding.CheckAndRepairAsync(chat, new() { ("futureResearchItem", Proposal) }, new[] { page },
            new Dictionary<int, IReadOnlyList<string>>(), 1, new GroundingOptions(Parallelism: 1, SecondCheck: false, Repair: true, SenseCheck: false));

        Assert.Equal("Field trials should test whether agents log every prompt.", Assert.Single(result.Fields).Text);
        Assert.Equal("drop_citation", Assert.Single(result.Repairs).Action);
        string repair = Assert.Single(chat.Prompts, p => p.Contains("You are correcting citations"));
        Assert.Contains("Never turn a proposal or a recommendation into a restatement of a finding.", repair);
    }
}

using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #571: the links of a concept map claim how studies relate, so each is checked as the sentence
/// "&lt;from&gt; &lt;label&gt; &lt;to&gt;" against the studies both boxes cite. A link no such study states is drawn dashed as the
/// review's own reading, and the methods say what was checked.
/// </summary>
public class ArtifactLinksTests
{
    private static readonly ReferencedPaper[] Papers =
    {
        new(1, "p1", "Secure pipelines", "", new[] { new DocumentChunk("p1", 1, "Governance layers audit every agent action in the pipeline.") }),
        new(2, "p2", "Agent workflows", "", new[] { new DocumentChunk("p2", 1, "Agents coordinate their workflow with reinforcement learning.") }),
        new(4, "p4", "Test automation", "", new[] { new DocumentChunk("p4", 1, "Hardware-in-the-loop tests measured fault coverage of the agents.") }),
    };

    /// <summary>Links that say "assessed via" are not supported; every other one is supported with a quote from the excerpt.</summary>
    private static string Check(string prompt)
    {
        string e1 = Regex.Match(prompt, @"^E1: (.*)$", RegexOptions.Multiline).Groups[1].Value;
        var sentences = Regex.Matches(prompt, @"^S\d+: (.*)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new { results = sentences.Select((text, i) => new
        {
            sentence = i + 1, verdict = text.Contains("assessed via") ? "not_supported" : "supported",
            excerpt = "E1", quote = e1[..Math.Min(30, e1.Length)], reason = "checked",
        }) });
    }

    private static ReviewArtifact Map(params ArtifactEdge[] edges) => new()
    {
        Kind = ArtifactKinds.ConceptMap,
        Nodes = new()
        {
            new() { Id = "n1", Label = "Secure orchestration forms a safety layer [1]." },
            new() { Id = "n2", Label = "Multi-agent workflows use reinforcement learning [2, 4]" },
            new() { Id = "n5", Label = "Hardware-in-the-loop tests with fault coverage [4]" },
        },
        Edges = edges.ToList(),
    };

    private static Task<List<ArtifactLinkCheck>> RunAsync(FakeChatService chat, ReviewArtifact map) =>
        ArtifactLinks.CheckAsync(chat, map, Papers, new Dictionary<int, IReadOnlyList<string>>());

    [Fact]
    public void ALinkIsTheSentenceFromLabelToCitingOnlyTheStudiesBothBoxesCite()
    {
        var map = Map(new ArtifactEdge { From = "n2", To = "n5", Label = "assessed via" });
        var shared = ArtifactLinks.SharedReferences(map.Nodes[1], map.Nodes[2]);

        Assert.Equal(new[] { 4 }, shared);
        Assert.Equal("Multi-agent workflows use reinforcement learning assessed via Hardware-in-the-loop tests with fault coverage [4].",
            ArtifactLinks.Sentence(map, map.Edges[0], shared));
        Assert.Equal("Secure orchestration forms a safety layer", ArtifactLinks.Statement(map.Nodes[0].Label));
    }

    [Fact]
    public async Task ALinkBetweenBoxesWithNoSharedStudyIsTheReviewsReadingWithoutAModelCall()
    {
        var chat = new FakeChatService().RespondsWith(Check);
        var map = Map(new ArtifactEdge { From = "n1", To = "n5", Label = "deployed in" });

        var decision = Assert.Single(await RunAsync(chat, map));

        Assert.Empty(chat.Prompts);
        Assert.Equal(ArtifactLinks.Reading, decision.Check);
        Assert.Empty(decision.SharedReferences);
        Assert.Equal(ArtifactLinks.Reading, map.Edges[0].Check);
        Assert.Contains("n1 -.->|\"deployed in\"| n5", ArtifactBuilder.ToMermaid(map));
        Assert.Contains(@"\draw[->, thick, dashed] (n1)", ArtifactBuilder.ToTikz(map, s => s));
    }

    [Fact]
    public async Task ALinkASharedStudyStatesIsKeptSolidAndOneItDoesNotIsDashed()
    {
        var chat = new FakeChatService().RespondsWith(Check);
        var map = Map(new ArtifactEdge { From = "n2", To = "n5", Label = "tested with" }, new ArtifactEdge { From = "n2", To = "n5", Label = "assessed via" });

        var decisions = await RunAsync(chat, map);

        Assert.Equal(new[] { ArtifactLinks.Supported, ArtifactLinks.Reading }, decisions.Select(d => d.Check));
        Assert.All(decisions, d => Assert.All(d.Verdicts, v => Assert.Equal(4, v.Reference)));
        Assert.Contains("[4], cited by both boxes", decisions[0].Reason);
        string mermaid = ArtifactBuilder.ToMermaid(map);
        Assert.Contains("n2 -->|\"tested with\"| n5", mermaid);
        Assert.Contains("n2 -.->|\"assessed via\"| n5", mermaid);
        Assert.Equal(" Each of its 2 links was checked as a sentence against the studies that both of its boxes cite, without repair; 1 is supported, and the other 1 is drawn dashed as the review's own reading. The verdicts are in artifact-links.json.",
            ArtifactLinks.MethodsSentence(map));
        Assert.Equal("Dashed links are the review's own reading: no single included study states them.", ArtifactLinks.DashedNote(map));
    }

    [Fact]
    public async Task ASupportedVerdictWhoseQuoteIsNotInThePaperDoesNotKeepTheLink()
    {
        var chat = new FakeChatService().RespondsWith(p => Regex.Replace(Check(p), "\"quote\":\"[^\"]*\"", "\"quote\":\"words the paper never says\""));
        var map = Map(new ArtifactEdge { From = "n2", To = "n5", Label = "tested with" });

        var decision = Assert.Single(await RunAsync(chat, map));

        Assert.Equal(ArtifactLinks.Reading, decision.Check);
    }

    [Fact]
    public void RunsFromBeforeTheCheckAndArtifactsWithoutLinksKeepTheirOldText()
    {
        var old = Map(new ArtifactEdge { From = "n1", To = "n2", Label = "secures" });
        Assert.Equal("", ArtifactLinks.MethodsSentence(old));
        Assert.Equal("", ArtifactLinks.DashedNote(old));
        Assert.Contains("n1 -->|\"secures\"| n2", ArtifactBuilder.ToMermaid(old));
        Assert.Equal("", ArtifactLinks.MethodsSentence(new ReviewArtifact { Kind = ArtifactKinds.Table }));

        old.Edges[0].Check = ArtifactLinks.Supported;
        Assert.Equal(" Its one link was checked as a sentence against the studies that both of its boxes cite, without repair; it is supported. The verdicts are in artifact-links.json.",
            ArtifactLinks.MethodsSentence(old));
    }
}

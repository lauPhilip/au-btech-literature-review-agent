using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #248: a repair can leave a paragraph that no longer reads well, as in a live run: "Three dominant
/// architectures emerge: master orchestrator and hierarchical models". Every repaired paragraph is read for sense once,
/// a paragraph that does not read well is rewritten once around its supported claims, and every citation in the
/// rewrite is checked again; the rewrite stays only when all of them are supported.
/// </summary>
public class RepairSenseTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "sense-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Before = "Three dominant architectures emerge: master orchestrator, hierarchical and swarm models [1]. One model, the master orchestrator, is also described in [2].";
    private const string Repaired = "Three dominant architectures emerge: master orchestrator and hierarchical models [1].";
    private const string Rewrite = "Two architectures are common, the master orchestrator and hierarchical models [1], and the master orchestrator coordinates the agents [2].";
    private const string BadSense = """{"readsWell":false,"problem":"It promises three architectures and names two."}""";

    private static readonly ReferencedPaper[] Sources =
    {
        new(1, "web1", "Vendor documentation 1", "", new[] { new DocumentChunk("web1", 1, "Two architectures are common: the master orchestrator and hierarchical models.") }),
        new(2, "web2", "Vendor documentation 2", "", new[] { new DocumentChunk("web2", 1, "The master orchestrator coordinates the agents.") }),
    };

    /// <summary>A sentence that names swarms is partly supported; everything else is supported with a quote from the excerpt.</summary>
    private static string Check(string prompt)
    {
        string e1 = Regex.Match(prompt, @"^E1: (.*)$", RegexOptions.Multiline).Groups[1].Value;
        int n = Regex.Matches(prompt, @"^S\d+: ", RegexOptions.Multiline).Count;
        string verdict = prompt.Contains("swarm") ? "partially_supported" : "supported";
        return System.Text.Json.JsonSerializer.Serialize(new { results = Enumerable.Range(1, n).Select(i => new { sentence = i, verdict, excerpt = "E1", quote = e1[..Math.Min(30, e1.Length)], reason = "checked" }) });
    }

    private static FakeChatService Model(string sense = BadSense, string rewrite = Rewrite) => new FakeChatService().RespondsWith(p =>
        p.Contains("You are correcting citations") ? $$"""{"repairs":[{"id":1,"action":"rewrite","sentence":"{{Repaired}}","reason":"narrowed"}]}"""
        : p.Contains("after an automated citation repair changed some of its sentences") ? sense
        : p.Contains("so that it reads well again") ? System.Text.Json.JsonSerializer.Serialize(new { text = rewrite })
        : Check(p));

    private static GroundingOptions Options(CheckCache? cache = null) =>
        new(Parallelism: 1, SecondCheck: false, Repair: true, Review: "grey literature review", Cache: cache);

    private static Task<GroundingResult> RunAsync(FakeChatService chat, CheckCache? cache = null) =>
        Grounding.CheckAndRepairAsync(chat, new() { ("synthesis-1", Before) }, Sources, new Dictionary<int, IReadOnlyList<string>>(), 2, Options(cache));

    [Fact]
    public async Task ARepairedParagraphThatReadsBadlyIsRewrittenAndCheckedAgain()
    {
        var chat = Model();

        var result = await RunAsync(chat);

        Assert.Equal(Rewrite, Assert.Single(result.Fields).Text);
        var sense = Assert.Single(result.Sense!);
        Assert.Equal((RepairSense.Rewritten, Repaired + " One model, the master orchestrator, is also described in [2].", Rewrite), (sense.Outcome, sense.Before, sense.After));
        Assert.Contains("It promises three architectures and names two.", sense.Note);
        // Every citation of the rewrite was checked again, under its new sentence, and the old verdicts are gone.
        Assert.Equal(new[] { (1, Rewrite), (2, Rewrite) }, result.Checks.Select(c => (c.Reference, c.Sentence)));
        Assert.All(result.Checks, c => Assert.Equal(CitationSupportChecker.Supported, c.Verdict));
        Assert.Contains(chat.Prompts, p => p.Contains("You are checking citations") && p.Contains(Rewrite));
        // The rewrite works from the supported claims only, and every prompt names the review as it is.
        string rewritePrompt = Assert.Single(chat.Prompts, p => p.Contains("so that it reads well again"));
        Assert.Contains(Repaired, rewritePrompt);
        Assert.DoesNotContain("swarm", rewritePrompt);
        Assert.DoesNotContain(chat.Prompts, p => p.Contains("systematic literature review"));
        Assert.Contains("You are reading one paragraph of a grey literature review", Assert.Single(chat.Prompts, p => p.Contains("Judge only whether the paragraph still reads well")));
    }

    [Fact]
    public async Task ARepairedParagraphThatReadsWellStaysAsItIs()
    {
        var result = await RunAsync(Model(sense: """{"readsWell":true,"problem":""}"""));

        Assert.Equal(Repaired + " One model, the master orchestrator, is also described in [2].", Assert.Single(result.Fields).Text);
        Assert.Equal(RepairSense.ReadsWell, Assert.Single(result.Sense!).Outcome);
    }

    [Fact]
    public async Task ARewriteWhoseCitationsAreNotAllSupportedIsKeptBack()
    {
        // The rewrite brings the swarm claim back; the check is not relaxed for it, so the repaired paragraph stays.
        var result = await RunAsync(Model(rewrite: "Master orchestrator, hierarchical and swarm models are common [1]. The master orchestrator coordinates the agents [2]."));

        Assert.Equal(Repaired + " One model, the master orchestrator, is also described in [2].", Assert.Single(result.Fields).Text);
        var sense = Assert.Single(result.Sense!);
        Assert.Equal(RepairSense.KeptBack, sense.Outcome);
        Assert.Contains("[1] partially_supported", sense.Note);
        Assert.All(result.Checks, c => Assert.Equal(CitationSupportChecker.Supported, c.Verdict));
    }

    [Fact]
    public async Task ARewriteThatAddsACitationIsSentBackAndThenKeptBack()
    {
        var result = await RunAsync(Model(rewrite: Rewrite.Replace("[2]", "[3]")));

        Assert.Equal(RepairSense.KeptBack, Assert.Single(result.Sense!).Outcome);
        Assert.Contains("the rewrite failed", result.Sense![0].Note);
    }

    [Fact]
    public async Task OnARerunTheSenseCheckAndTheRewriteComeFromTheCache()
    {
        var cache = new CheckCache(new ReviewCache(new CacheOptions { Folder = _root }, _root), "fake-model");
        await RunAsync(Model(), cache);

        var rerun = Model();
        var result = await RunAsync(rerun, cache);

        Assert.Equal(Rewrite, Assert.Single(result.Fields).Text);
        Assert.Equal(RepairSense.Rewritten, Assert.Single(result.Sense!).Outcome);
        Assert.DoesNotContain(rerun.Prompts, p => p.Contains("after an automated citation repair") || p.Contains("so that it reads well again") || p.Contains("You are checking citations"));
    }

    [Fact]
    public async Task AOneSentenceParagraphIsNotReadForSense()
    {
        var chat = Model();

        var result = await Grounding.CheckAndRepairAsync(chat, new() { ("artifact-n1", "Master orchestrator, hierarchical and swarm models [1].") }, Sources,
            new Dictionary<int, IReadOnlyList<string>>(), 2, Options());

        Assert.Empty(result.Sense!);
        Assert.DoesNotContain(chat.Prompts, p => p.Contains("after an automated citation repair"));
    }
}

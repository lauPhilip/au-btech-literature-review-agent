using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #248: the re-check of a repaired sentence used to leave out the review's name and the cache, so a grey
/// literature sentence was re-checked as a "systematic literature review" and asked again on every rerun. The
/// re-check now uses the same wording and cache as the first check.
/// </summary>
public class RepairRecheckTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "recheck-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Logs = "In this release every tool call is logged.";
    private const string Partly = """{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"every tool call is logged","reason":"tool calls only"}]}""";
    private const string Supported = """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"every tool call is logged","reason":"stated"}]}""";
    private const string Rewrite = """{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log every tool call [1].","reason":"narrowed"}]}""";

    private static ReferencedPaper Page(int n, string text) =>
        new(n, $"web{n}", $"Vendor documentation {n}", "", new[] { new DocumentChunk($"web{n}", 1, text) });

    private static List<(string Field, string Text)> Field() => new() { ("synthesis", "Agents log every tool call and every prompt [1].") };

    private static FakeChatService Model() => new FakeChatService().RespondsWith(p =>
        p.Contains("You are correcting citations") ? Rewrite
        : p.Contains("Agents log every tool call and every prompt [1].") ? Partly
        : Supported);

    [Fact]
    public async Task AGreyLiteratureSentenceIsCheckedAgainWithTheGreyWording()
    {
        var chat = Model();

        var result = await Grounding.CheckAndRepairAsync(chat, Field(), new[] { Page(1, Logs) }, new Dictionary<int, IReadOnlyList<string>>(), 1,
            new GroundingOptions(Parallelism: 1, SecondCheck: false, Repair: true, Review: "grey literature review"));

        Assert.Equal("Agents log every tool call [1].", Assert.Single(result.Fields).Text);
        Assert.Equal(CitationSupportChecker.Supported, Assert.Single(result.Checks).Verdict);
        var checks = chat.Prompts.Where(p => p.Contains("You are checking citations")).ToList();
        Assert.Equal(2, checks.Count); // the first check and the re-check of the repaired sentence
        Assert.All(checks, p => Assert.Contains("You are checking citations in a grey literature review.", p));
        Assert.Contains("You are correcting citations in a grey literature review.", Assert.Single(chat.Prompts, p => p.Contains("You are correcting citations")));
        Assert.DoesNotContain(chat.Prompts, p => p.Contains("systematic literature review"));
    }

    [Fact]
    public async Task TheReCheckIsTakenFromTheCacheOnARerun()
    {
        var cache = new CheckCache(new ReviewCache(new CacheOptions { Folder = _root }, _root), "fake-model");
        var options = new GroundingOptions(Parallelism: 1, SecondCheck: false, Repair: true, Review: "grey literature review", Cache: cache);
        var first = Model();
        await Grounding.CheckAndRepairAsync(first, Field(), new[] { Page(1, Logs) }, new Dictionary<int, IReadOnlyList<string>>(), 1, options);
        Assert.Equal(2, first.Prompts.Count(p => p.Contains("You are checking citations")));

        var rerun = Model();
        var result = await Grounding.CheckAndRepairAsync(rerun, Field(), new[] { Page(1, Logs) }, new Dictionary<int, IReadOnlyList<string>>(), 1, options);

        Assert.Equal("Agents log every tool call [1].", Assert.Single(result.Fields).Text);
        Assert.DoesNotContain(rerun.Prompts, p => p.Contains("You are checking citations")); // both checks came from the cache
    }
}

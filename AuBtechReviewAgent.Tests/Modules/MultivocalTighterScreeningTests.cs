using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Stricter multivocal screening: the first screening asks for positive evidence (multivocal only, the systematic
/// review's steps unchanged), the example's criteria are narrow, broad criteria are warned about at planning, and a
/// pilot of the first 30 decisions of a pool warns when more than half are included.
/// </summary>
public class MultivocalTighterScreeningTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrtight-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static FakeChatService Model() => new FakeChatService().RespondsWith(prompt =>
        prompt.Split("- Title: ")[1].Split('\n')[0].Contains("agent")
            ? """{"decision":"Included","reasoning":"On agents.","briefSummary":"A post.","confidence":"high"}"""
            : """{"decision":"Excluded","reasoning":"Off topic.","briefSummary":"A post.","confidence":"high","exclusionReason":"off-topic"}""");

    [Fact]
    public async Task TheFirstScreeningAsksForPositiveEvidenceAndTheSecondKeepsTheSharedSteps()
    {
        var chat = Model();
        var blog = new GreyRecord("x:1", "Memory for agent loops", "", "https://a.example.org/1", "Ann", "a.example.org", "blogs", null, new Dictionary<string, long>());
        var paper = blog with { Kind = MultivocalSearcher.FormalKind };

        await MultivocalScreener.ScreenGreyAsync(chat, blog, "About agents", "", secondScreener: false);
        await MultivocalScreener.ScreenGreyAsync(chat, paper, "About agents", "", secondScreener: false);
        await MultivocalScreener.ScreenGreyAsync(chat, blog, "About agents", "", secondScreener: true);

        Assert.All(chat.Prompts.Take(2), p =>
        {
            Assert.Contains(MultivocalScreener.FirstProcedure.Trim(), p);
            Assert.Contains("Decide Included only when there is positive evidence", p);
            Assert.Contains("If the record has no summary and the title alone gives no positive evidence", p);
            Assert.DoesNotContain(PrismaReviewEngine.ScreeningProcedure(false).Trim(), p);
        });
        Assert.Contains(PrismaReviewEngine.ScreeningProcedure(true).Trim(), chat.Prompts[2]);
        Assert.DoesNotContain(MultivocalScreener.FirstProcedure.Trim(), chat.Prompts[2]);
        Assert.Equal(("grey-screening-v3", "formal-screening-v3"), (MultivocalScreener.PromptVersion, MultivocalScreener.FormalPromptVersion));
    }

    [Fact]
    public void TheSystematicReviewsFirstScreeningStepsAreUnchanged()
    {
        string steps = PrismaReviewEngine.ScreeningProcedure(false);
        Assert.Contains("1. Determine if it should be Included or Excluded.", steps);
        Assert.DoesNotContain("positive evidence", steps);
    }

    [Fact]
    public void TheExamplesCriteriaRaiseNoWarningAndTheOldOnesDo()
    {
        Assert.Empty(MultivocalCriteriaCheck.Warnings(MultivocalPlan.Example()));
        Assert.Contains("main subject", MultivocalPlan.Example().InclusionCriteria);
        Assert.Contains("release notes and changelogs", MultivocalPlan.Example().ExclusionCriteria);

        var old = MultivocalCriteriaCheck.Warnings(
            "Sources about engineering the context given to LLM agents (what goes into the context window, memory, retrieval, tool descriptions, compression or summarisation), with practical experience, guidance, an evaluation or a tool.",
            "Sources only about the wording of prompts for single chat requests; marketing pages without technical content; sources not in English.");
        Assert.Equal(2, old.Count);
        Assert.StartsWith("The inclusion criterion is mostly a list of alternatives.", old[0]);
        Assert.Contains("\"or a tool\" and \"practical experience\"", old[1]);
    }

    [Theory]
    [InlineData("", "There is no exclusion criterion.")]
    [InlineData("Non-English sources.", "The exclusion criterion is very short.")]
    public void AnEmptyOrVeryShortExclusionIsWarnedAbout(string exclusion, string warning)
    {
        var warnings = MultivocalCriteriaCheck.Warnings("Sources whose main subject is how an agent's memory is managed.", exclusion);
        Assert.StartsWith(warning, Assert.Single(warnings));
    }

    [Theory]
    [InlineData("Any source on agents.", "\"any source\"")]
    [InlineData("Posts about agents, memory, etc.", "\"etc\"")]
    public void ACatchAllPhraseIsWarnedAbout(string inclusion, string phrase) =>
        Assert.Contains(MultivocalCriteriaCheck.Warnings(inclusion, "Sources not in English, and sources that are only marketing."), w => w.Contains(phrase));

    [Fact]
    public void AWordInsideAnotherIsNotACatchAllPhrase() =>
        Assert.Empty(MultivocalCriteriaCheck.Warnings("Sources whose main subject is the context of an agent, with sketches of its design.",
            "Sources not in English, and sources that are only marketing."));

    [Theory]
    [InlineData(30, 16, true)]
    [InlineData(30, 15, false)] // exactly half is not more than half
    [InlineData(29, 29, false)] // a pool smaller than the pilot is not checked
    [InlineData(60, 16, true)]  // only the first 30 count
    public void ThePilotWarnsWhenMoreThanHalfOfTheFirstThirtyAreIncluded(int pool, int includedFirst, bool warns)
    {
        var decisions = Enumerable.Range(0, pool).Select(i => i < includedFirst);
        string? warning = MultivocalPilotCheck.Warning(decisions, formal: false);
        Assert.Equal(warns, warning != null);
        if (warns) Assert.Equal($"More than half of the first 30 grey sources were included at the first screening ({includedFirst} of 30). The criteria may be broad.", warning);
        Assert.Null(MultivocalPilotCheck.Warning(Enumerable.Range(0, 60).Select(i => i >= 30), formal: true));
    }

    private sealed class Source(List<GreyRecord> records) : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(records);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(10, false)]
    public async Task AScreeningRecordsThePilotWarningOnFileWithoutExtraCalls(int onTopic, bool warns)
    {
        var records = Enumerable.Range(0, 30).Select(i => new GreyRecord($"x:{i}", i < onTopic ? $"Memory for agent loops {i}" : $"Baking bread, part {i}",
            $"Post {i}.", $"https://s{i}.example.org/p", $"P{i}", $"s{i}.example.org", "blogs", null, new Dictionary<string, long>())).ToList();
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source(records));
        await searcher.SearchAsync(runId, key);
        var chat = Model();
        var screener = new MultivocalScreener(store, planner, searcher, () => chat, parallelism: 3);

        var file = await screener.ScreenAsync(runId, key);

        Assert.Equal(warns, file.Warnings != null);
        if (warns) Assert.Contains($"({onTopic} of 30)", Assert.Single(file.Warnings!));
        Assert.Equal(30 + file.Sources.Count(s => s.Second != null), chat.Prompts.Count);
        Assert.Equal(file.Warnings, screener.Load(runId)!.Warnings); // on file, for the run view
        Assert.Equal("Screened", store.LoadHeader(runId)!.Stage); // the run is not stopped
    }
}

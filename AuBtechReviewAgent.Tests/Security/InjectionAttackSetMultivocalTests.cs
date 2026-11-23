using System.Text;
using AuBtechReviewAgent;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The attack set's pages (#373) run through a whole grey literature review, from its plan to the checked paper, with
/// the module kit's scripted fake model. Each page is a found source served at /&lt;id&gt;, and the off-topic paper of the
/// set comes in as a search result whose summary claims to meet the criteria. The fake model includes every source
/// whose title starts with "Agent" and scores every quality item 1, whatever the page says.
/// </summary>
public class InjectionAttackSetMultivocalTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "attackset-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Host = "https://attack.example.org/";

    private sealed class AttackWeb : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = AttackSet.Pages.FirstOrDefault(c => request.RequestUri!.AbsolutePath == "/" + c.Id);
            return Task.FromResult(page == null ? Respond(System.Net.HttpStatusCode.NotFound, "") : Respond(System.Net.HttpStatusCode.OK, page.Text));
        }

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Respond(System.Net.HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    private sealed class AttackSource : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(AttackSet.Pages
            .Select(c => new GreyRecord("a:" + c.Id, c.Title, "A post on how agents manage their context.", Host + c.Id, "Ana", "attack.example.org", "blogs", new DateTime(2025, 5, 1), new Dictionary<string, long>()))
            .Append(OffTopic())
            .ToList());

        private static GreyRecord OffTopic()
        {
            var c = AttackSet.Case("criteria-met");
            return new GreyRecord("a:" + c.Id, c.Title, c.Text, Host + c.Id, "Eve", "attack.example.org", "blogs", null, new Dictionary<string, long>());
        }
    }

    private static string Respond(string prompt) =>
        prompt.Contains("SOURCE TARGET DATA") && !prompt.Contains("- Title: Agent")
            ? """{"decision":"Excluded","reasoning":"Agronomy.","briefSummary":"A crop post.","confidence":"high","exclusionReason":"off-topic"}"""
            : ModuleKit.RespondMultivocal(prompt);

    private sealed record GreyRun(RunStore Store, Guid RunId, MultivocalScreener Screener, MultivocalPages Pages, MultivocalQualityAssessor Quality,
        MultivocalReporter Reporter, MultivocalWriter Writer, List<string> Prompts);

    private async Task<GreyRun> RunAsync()
    {
        var store = new RunStore(_root);
        var model = new FakeChatService().RespondsWith(Respond);
        IChatCompletionService Chat() => LlmRecording.Current ?? (IChatCompletionService)model;
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example("grey");
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);

        var searcher = new MultivocalSearcher(store, planner, _ => new AttackSource());
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new AttackWeb(), _ => Task.CompletedTask));
        var screener = new MultivocalScreener(store, planner, searcher, Chat, parallelism: 1);
        var quality = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, Chat, parallelism: 1);
        var mapper = new MultivocalMapper(store, planner, searcher, quality, Chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, quality, mapper, pages, Chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, quality, mapper, extractor, pages, Chat);
        var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, quality, mapper, extractor, synthesiser);
        var writer = new MultivocalWriter(store, reporter, pages, Chat, "fake-model", parallelism: 1);
        var runner = new MultivocalRunner(store, searcher, screener, quality, mapper, extractor, synthesiser, pages, writer, () => model, "fake-model");
        await runner.RunAsync(runId, key);
        Assert.Null(runner.StatusOf(runId)?.Error);
        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.ToList();
        return new GreyRun(store, runId, screener, pages, quality, reporter, writer, prompts);
    }

    [Fact]
    public async Task EveryPoisonedPageIsFlaggedOnFileAndTheScriptedDecisionsStand()
    {
        var run = await RunAsync();
        var screening = run.Screener.Load(run.RunId)!.Sources;
        var kept = run.Pages.Load(run.RunId).Pages;
        var quality = run.Quality.Load(run.RunId)!.Sources;

        var offTopic = Assert.Single(screening, s => s.Url == Host + "criteria-met");
        Assert.Equal("Excluded", offTopic.Decision);
        Assert.Equal(AttackSet.Case("criteria-met").Flags, offTopic.InjectionFlags);
        foreach (var c in AttackSet.Pages)
        {
            Assert.Equal(c.Decision, Assert.Single(screening, s => s.Url == Host + c.Id).Decision);
            var page = Assert.Single(kept, p => p.Url == Host + c.Id);
            Assert.True(c.Flags.SequenceEqual(page.InjectionFlags?.Select(f => f.Pattern) ?? Array.Empty<string>()), c.Id);
            Assert.Equal(HiddenTextRemoved.Of(c.Invisible, c.HiddenElements), page.HiddenText);
            // Every quality item has the scripted score: the page did not raise it.
            var assessed = Assert.Single(quality, q => q.Url == Host + c.Id);
            var scored = assessed.Answers.Where(a => GreyQualityChecklist.ModelItems.Any(i => i.Id == a.Id)).ToList();
            Assert.NotEmpty(scored);
            Assert.All(scored, a => Assert.Equal(1, a.Score));
        }
    }

    [Fact]
    public async Task NoPromptCarriesHiddenTextOrInvisibleCharactersAndTheGapsReachOnlyInsideTheMarkers()
    {
        var run = await RunAsync();

        Assert.NotEmpty(run.Prompts);
        Assert.All(run.Prompts, p => Assert.True(InjectionAttackSetTests.HasNoInvisibleCharacters(p)));
        foreach (string hidden in AttackSet.Pages.SelectMany(c => c.NotToModel))
            Assert.DoesNotContain(run.Prompts, p => p.Contains(hidden, StringComparison.Ordinal));
        foreach (var c in AttackSet.Pages.Where(c => c.Gap != null))
        {
            var reached = run.Prompts.Where(p => p.Contains(c.ReachesModel!, StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(reached); // the known gap: the text is not taken out
            Assert.All(reached, p =>
            {
                int at = p.IndexOf(c.ReachesModel!, StringComparison.Ordinal);
                Assert.True(p.LastIndexOf(PromptSafety.OpenTag, at, StringComparison.Ordinal) > p.LastIndexOf(PromptSafety.CloseTag, at, StringComparison.Ordinal), c.Id);
            });
        }
        Assert.Contains(run.Prompts, p => p.Contains(AttackSet.Case("page-clean-control").Kept!.Split('\n')[1], StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStatementOnAiUseCountsTheFlaggedPagesAndTheBenignPageIsInThePaper()
    {
        var run = await RunAsync();
        var input = run.Reporter.LoadInput(run.RunId)!;
        var withText = input.Pages.Pages.Where(p => !string.IsNullOrEmpty(p.TextFile)).ToList();
        int flagged = AttackSet.Pages.Count(c => c.Flags.Count > 0);

        string statement = MultivocalPaper.AiUse(input, run.Writer.Load(run.RunId)!);

        Assert.Equal(AttackSet.Pages.Count(), withText.Count);
        Assert.Contains(AiUseStatement.FlaggedSources(flagged, withText.Count, "kept text", "source", "sources"), statement);
        string bib = ModuleKit.Text(ModuleKit.Unzip(run.Writer.Archive(run.RunId))[GreyReferences.BibFile]);
        Assert.Contains(AttackSet.Case("page-benign").Title.Split(':')[1].Trim(), bib);
    }
}

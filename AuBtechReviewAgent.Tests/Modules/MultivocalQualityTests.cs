using System.Net;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The quality checklist for grey sources (MLR block E, G11, Table 7 of Garousi et al. 2019), offline: the 20 items,
/// the three decided in code, quotes that must be in the kept page, and a whole assessment with a fake web and model.
/// </summary>
public class MultivocalQualityTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrquality-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public void TheChecklistHasTheTwentyItemsOfTable7()
    {
        var groups = GreyQualityChecklist.Items.GroupBy(i => i.Group).Select(g => g.Count()).ToList();
        Assert.Equal(new[] { 4, 6, 4, 1, 1, 2, 1, 1 }, groups);
        Assert.Equal(20, GreyQualityChecklist.MaxPoints);
        Assert.Equal(new[] { "4.1", "7.1", "8.1" }, GreyQualityChecklist.Items.Where(i => i.ByCode).Select(i => i.Id));
        Assert.Equal(17, GreyQualityChecklist.ModelItems.Count);
        Assert.Equal(MultivocalGuidelines.QualityPointsMax, GreyQualityChecklist.MaxPoints);
    }

    [Fact]
    public void TheTotalsOfTable9PassTheThresholdInTheirOrder()
    {
        // Table 9: GL1 13, GL2 19, GL3 16, GL4 15.5, GL5 12 of 20 points; with the paper's threshold of 10 all pass.
        var totals = new Dictionary<string, double> { ["GL1"] = 13, ["GL2"] = 19, ["GL3"] = 16, ["GL4"] = 15.5, ["GL5"] = 12 };
        Assert.Equal(new[] { "GL2", "GL3", "GL4", "GL1", "GL5" }, totals.OrderByDescending(t => t.Value).Select(t => t.Key));
        Assert.All(totals.Values, t => Assert.Equal("Passed", MultivocalQualityAssessor.OutcomeFor(t, 10)));
        Assert.Equal(new[] { "GL1", "GL5" }, totals.Where(t => MultivocalQualityAssessor.OutcomeFor(t.Value, 14) != "Passed").Select(t => t.Key));
        Assert.Equal("Passed", MultivocalQualityAssessor.OutcomeFor(10, 10)); // at the threshold passes
    }

    [Theory]
    [InlineData("white-papers", 1.0)]
    [InlineData("theses", 1.0)]
    [InlineData("qa", 0.5)]
    [InlineData("documentation", 0.5)]
    [InlineData("blogs", 0.0)]
    [InlineData("code", 0.0)]
    public void TheOutletTierFollowsTable7(string kind, double score) =>
        Assert.Equal(score, GreyQualityChecklist.OutletTier(kind).Score);

    [Fact]
    public void ImpactUsesTheBestCountAndSaysWhenThereIsNone()
    {
        Assert.Equal(1.0, GreyQualityChecklist.Impact(new Dictionary<string, long> { ["stars"] = 13_884, ["forks"] = 5 }).Score);
        Assert.Equal(0.5, GreyQualityChecklist.Impact(new Dictionary<string, long> { ["score"] = 7, ["views"] = 300 }).Score);
        Assert.Equal(0.0, GreyQualityChecklist.Impact(new Dictionary<string, long> { ["citations"] = 0 }).Score);
        var none = GreyQualityChecklist.Impact(new Dictionary<string, long>());
        Assert.Equal(0.0, none.Score);
        Assert.StartsWith("Not available", none.Why);
        Assert.Equal(1.0, GreyQualityChecklist.Date(new DateTime(2025, 6, 1)).Score);
        Assert.Equal(0.0, GreyQualityChecklist.Date(null).Score);
    }

    private const string PageWords = "Agents forget their tools when the context window fills up. We measured this on forty agent runs.";

    private static GreyRecord Record(string url = "https://blog.example.org/ce", string title = "Context engineering for agents") =>
        new($"x:{url}", title, "A post.", url, "Ana", new Uri(url).Host, "blogs", null, new Dictionary<string, long>());

    private static List<ModelQualityAnswer> AllItems(double score, string? quote) =>
        GreyQualityChecklist.ModelItems.Select(i => new ModelQualityAnswer { Id = i.Id, Score = score, Reason = "Because.", Quote = quote }).ToList();

    [Fact]
    public void APointNeedsAQuoteFoundInThePage()
    {
        var found = MultivocalQualityAssessor.Score(Record(), AllItems(1, "Agents forget their tools when the context window fills up."), PageWords);
        Assert.Equal(17, found.Where(a => a.Basis == "model").Sum(a => a.Score));

        var invented = MultivocalQualityAssessor.Score(Record(), AllItems(1, "The author is a principal engineer at a large company."), PageWords);
        var lowered = invented.Where(a => a.Id != "3.3" && a.Basis != "code").ToList();
        Assert.All(lowered, a => Assert.Equal(0.0, a.Score));
        Assert.All(lowered, a => Assert.Equal("lowered", a.Basis));
        Assert.Contains("not found in the page", lowered[0].Reason);
        Assert.Equal(20, invented.Count); // the three code items are always there
    }

    [Fact]
    public void VestedInterestNeedsAQuoteToLowerItsScore()
    {
        var answers = AllItems(0, null);
        answers.Single(a => a.Id == "3.3").Score = 0; // "not free of vested interest", without evidence
        var scored = MultivocalQualityAssessor.Score(Record(), answers, PageWords);
        Assert.Equal(1.0, scored.Single(a => a.Id == "3.3").Score);

        answers.Single(a => a.Id == "3.3").Quote = "We measured this on forty agent runs.";
        Assert.Equal(0.0, MultivocalQualityAssessor.Score(Record(), answers, PageWords).Single(a => a.Id == "3.3").Score);

        // A missing answer scores 0.
        var missing = MultivocalQualityAssessor.Score(Record(), new List<ModelQualityAnswer>(), PageWords);
        Assert.Equal(0.0, missing.Single(a => a.Id == "2.1").Score);
    }

    private sealed class FakeWeb : HttpMessageHandler
    {
        private readonly Dictionary<string, (int Status, string Body)> _pages;
        public FakeWeb(Dictionary<string, (int, string)> pages) => _pages = pages;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _pages.TryGetValue(request.RequestUri!.ToString(), out var page) ? page : (404, "");
            return Task.FromResult(Respond(status, body));
        }

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Respond(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            Record("https://blog.example.org/good", "Context engineering for agents"),
            Record("https://blog.example.org/weak", "Agents, briefly"),
            Record("https://blog.example.org/private/x", "Agents in private"),
            Record("https://blog.example.org/bread", "Baking bread"),
        });
    }

    [Fact]
    public async Task IncludedSourcesAreKeptScoredAndJudgedAgainstTheThreshold()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);

        var model = new FakeChatService().RespondsWith(prompt =>
        {
            if (prompt.Contains("SOURCE TARGET DATA")) // screening: include the agent sources
            {
                bool include = prompt.Split("- Title: ")[1].Split('\n')[0].Contains("Agent", StringComparison.OrdinalIgnoreCase);
                return include
                    ? """{"decision":"Included","reasoning":"On topic.","briefSummary":"A post.","confidence":"high"}"""
                    : """{"decision":"Excluded","reasoning":"Off topic.","briefSummary":"A post.","confidence":"high","exclusionReason":"off-topic"}""";
            }
            bool good = prompt.Contains("We measured this on forty agent runs");
            var answers = AllItems(good ? 1 : 0, good ? "Agents forget their tools when the context window fills up." : null);
            return System.Text.Json.JsonSerializer.Serialize(new { answers = answers.Select(a => new { id = a.Id, score = a.Score, reason = a.Reason, quote = a.Quote }) });
        });
        var screener = new MultivocalScreener(store, planner, searcher, () => model, parallelism: 1);
        await screener.ScreenAsync(runId, key);

        var web = new FakeWeb(new()
        {
            ["https://blog.example.org/robots.txt"] = (200, "User-agent: *\nDisallow: /private/\n"),
            ["https://blog.example.org/good"] = (200, $"<html><body><main><p>{PageWords}</p></main></body></html>"),
            ["https://blog.example.org/weak"] = (200, "<html><body><main><p>Agents are neat, I think, and that is all I will say.</p></main></body></html>"),
        });
        var pages = new MultivocalPages(store, searcher, new PageFetcher(web, _ => Task.CompletedTask));
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model, model: "fake-model", parallelism: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => assessor.AssessAsync(runId, "wrong-key"));
        var file = await assessor.AssessAsync(runId, key);

        Assert.Equal(3, file.Sources.Count); // the bread post was excluded at screening
        var good = file.Sources.Single(s => s.Title == "Context engineering for agents");
        Assert.Equal("Passed", good.Outcome);
        Assert.Equal(17, good.Points); // 17 model items; no date, no counts and a blog (3rd tier) give 0 in code
        Assert.Equal(20, good.Answers.Count);
        Assert.Equal("Below threshold", file.Sources.Single(s => s.Title == "Agents, briefly").Outcome);
        var unread = file.Sources.Single(s => s.Title == "Agents in private");
        Assert.Equal("Not assessed", unread.Outcome);
        Assert.Contains("robots.txt", unread.NotAssessed);
        Assert.Equal(MultivocalQualityAssessor.StageAssessed, store.LoadHeader(runId)!.Stage);
        Assert.Equal(10, assessor.Load(runId)!.Threshold);

        var zip = RunArchive.Build(runId, store.FolderOf(runId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalQualityAssessor.QualityFile);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith(MultivocalPages.TextFolder));
    }
}

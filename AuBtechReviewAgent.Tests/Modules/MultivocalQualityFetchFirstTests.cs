using System.Net;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The quality step keeps every page before it asks the model anything, so a model slot never waits on a slow site,
/// and a source whose page could not be fetched is not assessed, with its reason, without a model call.
/// </summary>
public class MultivocalQualityFetchFirstTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrfetchfirst-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string PageWords = "Agents forget their tools when the context window fills up. We measured this on forty agent runs.";

    private static GreyRecord Record(string url, string title) =>
        new($"x:{url}", title, "A post.", url, "Ana", new Uri(url).Host, "blogs", null, new Dictionary<string, long>());

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            Record("https://a.example.org/one", "Agents one"),
            Record("https://b.example.org/two", "Agents two"),
            Record("https://c.example.org/three", "Agents three"),
            Record("https://a.example.org/private/x", "Agents in private"),
            Record("https://down.example.org/gone", "Agents offline"),
        });
    }

    /// <summary>A web that writes every page request into the shared log; one site cannot be reached at all.</summary>
    private sealed class Web : HttpMessageHandler
    {
        private readonly List<string> _log;
        public Web(List<string> log) => _log = log;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "down.example.org") throw new HttpRequestException("No such host is known.");
            if (uri.AbsolutePath == "/robots.txt")
                return Task.FromResult(Text(HttpStatusCode.OK, "User-agent: *\nDisallow: /private/\n"));
            lock (_log) _log.Add("fetch " + uri);
            return Task.FromResult(Text(HttpStatusCode.OK, $"<html><body><main><p>{PageWords}</p></main></body></html>"));
        }

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    [Fact]
    public async Task EveryPageIsKeptBeforeTheModelIsAskedAndAnUnfetchedPageCostsNoModelCall()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);

        var log = new List<string>();
        var model = new FakeChatService().RespondsWith(prompt =>
        {
            if (prompt.Contains("SOURCE TARGET DATA"))
                return """{"decision":"Included","reasoning":"On topic.","briefSummary":"A post.","confidence":"high"}""";
            lock (log) log.Add("model");
            var answers = GreyQualityChecklist.ModelItems.Select(i => new { id = i.Id, score = 1, reason = "Because.", quote = "We measured this on forty agent runs." });
            return System.Text.Json.JsonSerializer.Serialize(new { answers });
        });
        var screener = new MultivocalScreener(store, planner, searcher, () => model, parallelism: 1);
        await screener.ScreenAsync(runId, key);

        var pages = new MultivocalPages(store, searcher, new PageFetcher(new Web(log), _ => Task.CompletedTask));
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model, parallelism: 1);
        var file = await assessor.AssessAsync(runId, key);

        Assert.Equal(5, file.Sources.Count);
        int firstModel = log.IndexOf("model");
        Assert.Equal(3, log.Count(e => e.StartsWith("fetch ")));
        Assert.All(log.Take(firstModel), e => Assert.StartsWith("fetch ", e)); // every page before any model call
        Assert.Equal(3, log.Count(e => e == "model")); // none for the two pages that were not kept

        Assert.Equal(3, file.Count("Passed"));
        Assert.Contains("robots.txt", file.Sources.Single(s => s.Title == "Agents in private").NotAssessed);
        var offline = file.Sources.Single(s => s.Title == "Agents offline");
        Assert.Equal("Not assessed", offline.Outcome);
        Assert.StartsWith("the site's robots.txt could not be read", offline.NotAssessed); // its reason is on file
        Assert.Equal(file.Sources.Select(s => s.Title), new[] { "Agents one", "Agents two", "Agents three", "Agents in private", "Agents offline" });
    }
}

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Page snapshots of grey sources (MLR block D, decision 2), offline: robots.txt is read as RFC 9309 says, the
/// page text is the same every time for the same page, sign-ins and private addresses are refused, and the text
/// stays in the run folder while only its record goes into the archive.
/// </summary>
public class WebPageTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrpages-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    /// <summary>A web server in a dictionary: address → (status, type, body, redirect).</summary>
    private sealed class FakeWeb : HttpMessageHandler
    {
        private readonly Dictionary<string, (int Status, string Type, string Body, string? Location)> _pages;
        public List<string> Requested { get; } = new();

        public FakeWeb(Dictionary<string, (int, string, string, string?)> pages) => _pages = pages;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, type, body, location) = _pages.TryGetValue(url, out var page) ? page : (404, "text/plain", "", null);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, type) };
            if (location != null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }
    }

    private const string Article = """
        <html><head><title>Context engineering &amp; agents</title><script>var x = "<p>not text</p>";</script></head>
        <body><nav><a href="/">Home</a> <a href="/blog">Blog</a></nav>
        <main><h1>Context engineering</h1><p>Agents <em>forget</em> tools&nbsp;when the window fills.</p><aside>Subscribe!</aside>
        <p>Summaries help.</p></main><footer>© Example</footer></body></html>
        """;

    private static (PageFetcher Fetcher, FakeWeb Web, List<TimeSpan> Waits) Fetcher(Dictionary<string, (int, string, string, string?)> pages)
    {
        var web = new FakeWeb(pages);
        var waits = new List<TimeSpan>();
        return (new PageFetcher(web, wait => { waits.Add(wait); return Task.CompletedTask; }), web, waits);
    }

    [Fact]
    public void RobotsRulesForUsWinOverTheGeneralOnesAndTheLongestRuleDecides()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /

            User-agent: GPTBot
            User-agent: TraceableAI   # our group
            Disallow: /private
            Allow: /private/reports
            Disallow: /*.pdf$
            Disallow:
            Crawl-delay: 2
            """);

        Assert.True(rules.Allows("/blog/post"));
        Assert.False(rules.Allows("/private/notes"));
        Assert.True(rules.Allows("/private/reports/2025"));
        Assert.False(rules.Allows("/files/report.pdf"));
        Assert.True(rules.Allows("/files/report.pdf?download=1"));
        Assert.True(rules.Allows("/robots.txt"));
        Assert.Equal(TimeSpan.FromSeconds(2), rules.CrawlDelay);

        var others = RobotsRules.Parse("User-agent: *\nDisallow: /search\nAllow: /search/about\n");
        Assert.False(others.Allows("/search?q=x"));
        Assert.True(others.Allows("/search/about"));
        Assert.True(RobotsRules.Parse("User-agent: SomeoneElse\nDisallow: /\n").Allows("/anything"));
    }

    [Theory]
    [InlineData("/fish", "/fish.html", true)]
    [InlineData("/fish*", "/fishheads", true)]
    [InlineData("/*.php", "/folder/index.php", true)]
    [InlineData("/*.php$", "/folder/index.php?x=1", false)]
    [InlineData("/fish$", "/fish", true)]
    [InlineData("/fish$", "/fishy", false)]
    [InlineData("/a%20b", "/a b", true)]
    [InlineData("/Fish", "/fish", false)]
    public void RobotsPatternsMatchAsTheRfcShows(string pattern, string path, bool matches) =>
        Assert.Equal(matches, RobotsRules.Matches(pattern, path));

    [Fact]
    public void ThePageTextIsTheMainContentOnePieceToALineAndTheSameEveryTime()
    {
        string text = PageText.FromHtml(Article);

        Assert.Equal("Context engineering\nAgents forget tools when the window fills.\nSummaries help.", text);
        Assert.Equal(text, PageText.FromHtml(Article));
        Assert.Equal("Context engineering & agents", PageText.TitleOf(Article));

        // Without <main>: the body, without its navigation and footer.
        Assert.Equal("Hello\nWorld", PageText.FromHtml("<body><header>Site</header><div>Hello</div><p>World</p><footer>x</footer></body>"));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata service
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void OnlyPublicAddressesAreConnectedTo(string address, bool isPublic) =>
        Assert.Equal(isPublic, PageFetcher.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task APageIsFetchedOnlyWhenRobotsTxtAllowsIt()
    {
        var (fetcher, web, _) = Fetcher(new()
        {
            ["https://blog.example.org/robots.txt"] = (200, "text/plain", "User-agent: *\nDisallow: /drafts/\n", null),
            ["https://blog.example.org/posts/ce"] = (200, "text/html", Article, null),
            ["https://blog.example.org/drafts/ce"] = (200, "text/html", Article, null),
        });

        var page = await fetcher.FetchAsync("https://blog.example.org/posts/ce");
        var draft = await fetcher.FetchAsync("https://blog.example.org/drafts/ce");

        Assert.Null(page.NotFetched);
        Assert.StartsWith("Context engineering\nAgents forget", page.Text);
        Assert.Equal("the site's robots.txt does not allow it", draft.NotFetched);
        Assert.DoesNotContain("https://blog.example.org/drafts/ce", web.Requested);
        Assert.Single(web.Requested, u => u.EndsWith("/robots.txt")); // read once per site
    }

    [Fact]
    public async Task AnUnreadableRobotsTxtMeansNothingIsFetchedAndAMissingOneMeansAll()
    {
        var (fetcher, web, _) = Fetcher(new()
        {
            ["https://down.example.org/robots.txt"] = (503, "text/plain", "", null),
            ["https://down.example.org/a"] = (200, "text/html", Article, null),
            ["https://open.example.org/a"] = (200, "text/plain", "Plain notes.\r\n\r\nSecond line.", null),
        });

        Assert.Equal("the site's robots.txt could not be read, so nothing on the site is fetched", (await fetcher.FetchAsync("https://down.example.org/a")).NotFetched);
        Assert.DoesNotContain("https://down.example.org/a", web.Requested);
        Assert.Equal("Plain notes.\nSecond line.", (await fetcher.FetchAsync("https://open.example.org/a")).Text);
    }

    [Fact]
    public async Task SignInsRedirectsAndOtherTypesAreHandled()
    {
        var (fetcher, web, _) = Fetcher(new()
        {
            ["https://a.example.org/members/post"] = (302, "text/html", "", "/login?next=/members/post"),
            ["https://a.example.org/paywalled"] = (403, "text/html", "", null),
            ["https://a.example.org/moved"] = (301, "text/html", "", "https://b.example.org/new"),
            ["https://b.example.org/robots.txt"] = (200, "text/plain", "User-agent: TraceableAI\nDisallow: /new\n", null),
            ["https://a.example.org/logo.png"] = (200, "image/png", "png", null),
        });

        Assert.Equal("the address is a sign-in page", (await fetcher.FetchAsync("https://a.example.org/members/post")).NotFetched);
        Assert.Equal("the site needs a sign-in or refused access (403)", (await fetcher.FetchAsync("https://a.example.org/paywalled")).NotFetched);
        var moved = await fetcher.FetchAsync("https://a.example.org/moved");
        Assert.Equal("the site's robots.txt for TraceableAI does not allow it", moved.NotFetched); // the new site's rules apply
        Assert.Equal("https://b.example.org/new", moved.FinalUrl);
        Assert.StartsWith("not a web page, text or PDF (image/png)", (await fetcher.FetchAsync("https://a.example.org/logo.png")).NotFetched);
        Assert.Equal("not a web address", (await fetcher.FetchAsync("javascript:alert(1)")).NotFetched);
        Assert.DoesNotContain(web.Requested, u => u.Contains("/login"));
    }

    [Fact]
    public async Task EachSiteGetsAtMostOneRequestASecondOrItsCrawlDelay()
    {
        var (fetcher, _, waits) = Fetcher(new()
        {
            ["https://slow.example.org/robots.txt"] = (200, "text/plain", "User-agent: *\nCrawl-delay: 5\n", null),
            ["https://slow.example.org/a"] = (200, "text/html", Article, null),
            ["https://slow.example.org/b"] = (200, "text/html", Article, null),
            ["https://rude.example.org/robots.txt"] = (200, "text/plain", "User-agent: *\nCrawl-delay: 120\n", null),
        });

        await fetcher.FetchAsync("https://slow.example.org/a");
        await fetcher.FetchAsync("https://slow.example.org/b");

        Assert.Equal(2, waits.Count); // before /a (after robots.txt) and before /b
        Assert.True(waits[0] <= TimeSpan.FromSeconds(1) && waits[0] > TimeSpan.FromSeconds(0.5));
        Assert.True(waits[1] > TimeSpan.FromSeconds(4.5));
        Assert.Equal("the site asks for 120 seconds between requests", (await fetcher.FetchAsync("https://rude.example.org/x")).NotFetched);
    }

    private sealed class OneSource : IGreySource
    {
        private readonly List<GreyRecord> _records;
        public OneSource(params string[] urls) => _records = urls
            .Select(u => new GreyRecord($"x:{u}", "A post", "summary", u, "someone", new Uri(u).Host, "blogs", null, new Dictionary<string, long>()))
            .ToList();
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(_records);
    }

    private async Task<(RunStore Store, Guid RunId, string Key, MultivocalPages Pages, FakeWeb Web)> SearchedRun(Dictionary<string, (int, string, string, string?)> web)
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new OneSource("https://blog.example.org/posts/ce", "https://blog.example.org/drafts/ce"));
        await searcher.SearchAsync(runId, key);
        var (fetcher, fake, _) = Fetcher(web);
        return (store, runId, key, new MultivocalPages(store, searcher, fetcher), fake);
    }

    [Fact]
    public async Task AKeptPageHasItsTextInTheRunFolderAndOnlyItsRecordInTheArchive()
    {
        var (store, runId, key, pages, _) = await SearchedRun(new()
        {
            ["https://blog.example.org/robots.txt"] = (200, "text/plain", "User-agent: *\nDisallow: /drafts/\n", null),
            ["https://blog.example.org/posts/ce"] = (200, "text/html", Article, null),
        });

        var kept = await pages.KeepAsync(runId, key, "https://www.blog.example.org/posts/ce/"); // the same source, written differently
        var again = await pages.KeepAsync(runId, key, "https://blog.example.org/posts/ce");
        var refused = await pages.KeepAsync(runId, key, "https://blog.example.org/drafts/ce");

        string expected = PageText.FromHtml(Article);
        Assert.Null(kept.NotKept);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant(), kept.TextSha256);
        Assert.Equal(kept.TextSha256, again.TextSha256); // the same page gives the same snapshot
        Assert.Equal(expected, pages.ReadText(runId, again));
        Assert.StartsWith("https://web.archive.org/web/", kept.WaybackUrl);
        Assert.Equal("the site's robots.txt does not allow it", refused.NotKept);
        Assert.Equal(2, pages.Load(runId).Pages.Count); // keeping again replaced the first snapshot

        var zip = RunArchive.Build(runId, store.FolderOf(runId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalPages.PagesFile);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith(MultivocalPages.TextFolder));

        // A changed text file no longer passes as the snapshot.
        File.AppendAllText(Path.Join(store.FolderOf(runId), again.TextFile), " edited");
        Assert.Null(pages.ReadText(runId, again));
    }

    [Fact]
    public async Task OnlyPagesTheSearchesFoundCanBeKeptAndOnlyByThePlanner()
    {
        var (_, runId, key, pages, web) = await SearchedRun(new());

        await Assert.ThrowsAsync<InvalidOperationException>(() => pages.KeepAsync(runId, key, "http://169.254.169.254/latest/meta-data/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pages.KeepAsync(runId, "wrong-key", "https://blog.example.org/posts/ce"));
        Assert.Empty(web.Requested);
    }
}

using System.Diagnostics;
using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>arXiv answers bursts with 503: requests are retried, spaced out and sent one at a time.</summary>
public class ArxivSourceTests
{
    private const string Feed = """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <id>http://arxiv.org/abs/2607.02703v1</id>
            <title>LLMoxie: Exploring Agentic AI</title>
            <summary>Agents for scientific software.</summary>
            <published>2026-07-03T00:00:00Z</published>
            <author><name>Lucas Setiawan</name></author>
          </entry>
        </feed>
        """;

    private class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _codes;
        public List<DateTime> Requests { get; } = new();
        public int InFlight, MaxInFlight;
        public ScriptedHandler(params HttpStatusCode[] codes) => _codes = new(codes);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref InFlight);
            lock (Requests) { Requests.Add(DateTime.UtcNow); MaxInFlight = Math.Max(MaxInFlight, now); }
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref InFlight);
            HttpStatusCode code;
            lock (_codes) code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(code) { Content = new StringContent(code == HttpStatusCode.OK ? Feed : "busy") };
        }
    }

    [Fact]
    public async Task A503IsRetriedAndTheSearchStillSucceeds()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var source = new ArxivSource(new HttpClient(handler), TimeSpan.Zero, _ => TimeSpan.FromMilliseconds(10));

        var papers = await source.FetchPapersAsync("agentic ai", 5);

        Assert.Equal(3, handler.Requests.Count);
        var paper = Assert.Single(papers);
        Assert.Equal("10.48550/arXiv.2607.02703", paper.Doi);
        Assert.Single(source.LastRawResponses);
    }

    private class FeedHandler(string feed) : HttpMessageHandler
    {
        // Returned straight from an async method, as in ScriptedHandler: the caller owns and disposes the response.
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) };
        }
    }

    [Fact]
    public async Task ThePublishedVersionsDoiIsUsedWhenArxivHasOne()
    {
        // Issue #151: a journal_ref with the arXiv DOI gave "Applied Sciences ... https://doi.org/10.48550/arXiv...".
        const string published = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:arxiv="http://arxiv.org/schemas/atom">
              <entry>
                <id>http://arxiv.org/abs/2407.00334v2</id>
                <title>Serious play in engineering education</title>
                <summary>A study.</summary>
                <published>2024-07-01T00:00:00Z</published>
                <author><name>Ana Lopes</name></author>
                <arxiv:journal_ref>Applied Sciences, Volume 14, Issue 2</arxiv:journal_ref>
                <arxiv:doi>10.3390/app14020123</arxiv:doi>
              </entry>
              <entry>
                <id>http://arxiv.org/abs/2407.00335v1</id>
                <title>A preprint with a journal reference only</title>
                <summary>A study.</summary>
                <published>2024-07-01T00:00:00Z</published>
                <author><name>Ben Ng</name></author>
                <arxiv:journal_ref>Applied Sciences, Volume 14, Issue 2</arxiv:journal_ref>
              </entry>
            </feed>
            """;
        var source = new ArxivSource(new HttpClient(new FeedHandler(published)), TimeSpan.Zero);

        var papers = await source.FetchPapersAsync("serious play", 5);

        Assert.Equal("10.3390/app14020123", papers[0].Doi);
        Assert.Equal("https://arxiv.org/abs/2407.00334v2", papers[0].Url);
        Assert.Equal("10.48550/arXiv.2407.00335", papers[1].Doi);
    }

    [Fact]
    public async Task APersistent503StillFailsTheSourceSoTheLogShowsIt()
    {
        var codes = Enumerable.Repeat(HttpStatusCode.ServiceUnavailable, 10).ToArray();
        var source = new ArxivSource(new HttpClient(new ScriptedHandler(codes)), TimeSpan.Zero, _ => TimeSpan.Zero);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchPapersAsync("agentic ai", 5));
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task RequestsFromSeveralRunsGoOneAtATimeAndAreSpacedOut()
    {
        var handler = new ScriptedHandler();
        var spacing = TimeSpan.FromMilliseconds(150);
        var sources = Enumerable.Range(0, 3).Select(_ => new ArxivSource(new HttpClient(handler), spacing)).ToList();

        await Task.WhenAll(sources.Select(s => s.FetchPapersAsync("agentic ai", 5)));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.MaxInFlight);
        var times = handler.Requests.OrderBy(t => t).ToList();
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= spacing - TimeSpan.FromMilliseconds(15), $"requests {i - 1} and {i} were {(times[i] - times[i - 1]).TotalMilliseconds} ms apart");
    }
}

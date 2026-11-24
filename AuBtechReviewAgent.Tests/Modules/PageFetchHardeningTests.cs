using System.Net;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The page fetcher cannot be held up by one site: a page that drips in hits the deadline, a robots.txt that could not
/// be read closes its site for minutes rather than a day, and parallel requests to one site read its robots.txt once.
/// </summary>
public class PageFetchHardeningTests
{
    /// <summary>A body that sends one byte at a time, slowly, for ever (until the reader gives up).</summary>
    private sealed class DripStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            buffer.Span[0] = (byte)'a';
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A web server whose answers the test sets per path; it counts the robots.txt requests.</summary>
    private sealed class Web : HttpMessageHandler
    {
        public int RobotsReads;
        public int RobotsStatus = 200;
        public TaskCompletionSource? HoldRobots;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/robots.txt")
            {
                Interlocked.Increment(ref RobotsReads);
                if (HoldRobots != null) await HoldRobots.Task.WaitAsync(cancellationToken);
                return Text((HttpStatusCode)RobotsStatus, "User-agent: *\nAllow: /\n", "text/plain");
            }
            if (path == "/drip")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new DripStream()) { Headers = { ContentType = new("text/html") } },
                };
            return Text(HttpStatusCode.OK, "<html><body><main><p>Agents keep a running summary.</p></main></body></html>", "text/html");
        }

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Text(HttpStatusCode status, string body, string type) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    }

    [Fact]
    public async Task APageThatDripsInHitsTheDeadline()
    {
        var fetcher = new PageFetcher(new Web(), _ => Task.CompletedTask, deadline: TimeSpan.FromMilliseconds(300));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var page = await fetcher.FetchAsync("https://slow.example.org/drip");

        Assert.StartsWith("the site took more than", page.NotFetched);
        Assert.Equal("", page.Text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.Null((await fetcher.FetchAsync("https://slow.example.org/page")).NotFetched); // the site is not closed by it
    }

    [Fact]
    public async Task ARobotsTxtThatCouldNotBeReadIsTriedAgainAfterMinutesNotADay()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var web = new Web { RobotsStatus = 503 };
        var fetcher = new PageFetcher(web, _ => Task.CompletedTask, clock: () => now);

        Assert.Contains("robots.txt could not be read", (await fetcher.FetchAsync("https://down.example.org/a")).NotFetched);
        web.RobotsStatus = 200; // the site is back
        now += TimeSpan.FromMinutes(5);
        Assert.Contains("robots.txt could not be read", (await fetcher.FetchAsync("https://down.example.org/a")).NotFetched);
        Assert.Equal(1, web.RobotsReads);

        now += PageFetcher.RobotsRetryAfter;
        Assert.Null((await fetcher.FetchAsync("https://down.example.org/a")).NotFetched);
        Assert.Equal(2, web.RobotsReads);

        // A robots.txt that was read is kept for a day.
        now += PageFetcher.RobotsLifetime - TimeSpan.FromMinutes(1);
        Assert.Null((await fetcher.FetchAsync("https://down.example.org/b")).NotFetched);
        Assert.Equal(2, web.RobotsReads);
        now += TimeSpan.FromMinutes(2);
        Assert.Null((await fetcher.FetchAsync("https://down.example.org/c")).NotFetched);
        Assert.Equal(3, web.RobotsReads);
    }

    [Fact]
    public async Task ParallelRequestsToOneSiteReadItsRobotsTxtOnce()
    {
        var web = new Web { HoldRobots = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var fetcher = new PageFetcher(web, _ => Task.CompletedTask);

        var pages = Enumerable.Range(1, 6).Select(i => fetcher.FetchAsync($"https://busy.example.org/p{i}")).ToList();
        await Task.Delay(100);
        web.HoldRobots.SetResult();
        var fetched = await Task.WhenAll(pages);

        Assert.All(fetched, p => Assert.Null(p.NotFetched));
        Assert.Equal(1, web.RobotsReads);
    }
}

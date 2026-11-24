using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

// robots.txt, read once per site and kept for a day. One fetcher serves every run, so a read that failed is kept for
// a short time only (a site that was down for a minute is not closed to every run for a day), and parallel requests to
// one site share one read.
public sealed partial class PageFetcher
{
    /// <summary>How long a robots.txt that was read is kept.</summary>
    public static readonly TimeSpan RobotsLifetime = TimeSpan.FromHours(24);

    /// <summary>How long a robots.txt that could not be read (5xx, no answer, too slow) keeps its site closed.</summary>
    public static readonly TimeSpan RobotsRetryAfter = TimeSpan.FromMinutes(10);

    private sealed record RobotsEntry(RobotsRules Rules, DateTime Expires);

    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsEntry>>> _robots = new(StringComparer.OrdinalIgnoreCase);

    private async Task<RobotsRules> RobotsFor(Uri page)
    {
        string origin = page.GetLeftPart(UriPartial.Authority);
        while (true)
        {
            var read = _robots.GetOrAdd(origin, o => new Lazy<Task<RobotsEntry>>(() => ReadRobotsAsync(o)));
            RobotsEntry entry;
            try
            {
                entry = await read.Value;
            }
            catch
            {
                _robots.TryRemove(new KeyValuePair<string, Lazy<Task<RobotsEntry>>>(origin, read)); // the next request reads it again
                throw;
            }
            if (_clock() < entry.Expires) return entry.Rules;
            _robots.TryRemove(new KeyValuePair<string, Lazy<Task<RobotsEntry>>>(origin, read)); // only this read, not a newer one
        }
    }

    private async Task<RobotsEntry> ReadRobotsAsync(string origin)
    {
        RobotsRules rules = RobotsRules.DisallowAll;
        using var deadline = new FetchDeadline(_deadline);
        try
        {
            var robotsUri = new Uri(origin + "/robots.txt");
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                using var response = await PacedGetAsync(robotsUri, null, "text/plain", deadline);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is Uri location)
                {
                    robotsUri = Resolve(robotsUri, location);
                    if (!PrismaReviewEngine.IsWebAddress(robotsUri.ToString())) break;
                    continue;
                }
                if (response.IsSuccessStatusCode)
                {
                    using var buffer = new MemoryStream();
                    await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
                        await DocumentRAGUtility.CopyWithLimitAsync(input, buffer, RobotsRules.MaxLength, deadline.Token);
                    rules = RobotsRules.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
                }
                else if (status is >= 400 and < 500) rules = RobotsRules.AllowAll; // no robots.txt (RFC 9309, 2.3.1.3)
                break; // 5xx: assume nothing may be fetched (2.3.1.4)
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException or IOException)
        {
            rules = RobotsRules.DisallowAll;
        }
        bool failed = ReferenceEquals(rules, RobotsRules.DisallowAll);
        return new RobotsEntry(rules, _clock() + (failed ? RobotsRetryAfter : RobotsLifetime));
    }

    /// <summary>
    /// The deadline of one fetch: it starts with the first request, so time spent waiting for the site's turn does not
    /// count, and redirects share it.
    /// </summary>
    private sealed class FetchDeadline : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly TimeSpan _limit;
        private bool _started;

        public FetchDeadline(TimeSpan limit) => _limit = limit;

        public CancellationToken Token => _source.Token;
        public bool Passed => _source.IsCancellationRequested;

        public void Start()
        {
            if (_started) return;
            _started = true;
            _source.CancelAfter(_limit);
        }

        public void Dispose() => _source.Dispose();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace AuBtechReviewAgent;

/// <summary>
/// How the scholarly databases have been answering lately, for the dashboard ("arXiv slow, Semantic Scholar
/// rate-limited") and /health. Every request through <see cref="OpenSourceHttp"/> is recorded per host: when it
/// last worked, the last problem and how long answers took. Kept in memory only; it says nothing about who asked.
/// </summary>
public static class SourceStatus
{
    /// <summary>An answer slower than this counts as slow.</summary>
    public static readonly TimeSpan SlowAnswer = TimeSpan.FromSeconds(10);

    /// <summary>Problems older than this are no longer shown.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(30);

    public enum Problem { None, RateLimited, ServerError, Timeout, Unreachable, Slow }

    public sealed record HostState(string Host, DateTime? LastOkUtc, DateTime? LastProblemUtc, Problem LastProblem, TimeSpan? LastDuration, int RecentProblems);

    private sealed class Entry
    {
        public DateTime? LastOkUtc;
        public DateTime? LastProblemUtc;
        public Problem LastProblem;
        public TimeSpan? LastDuration;
        public readonly Queue<DateTime> Problems = new();
    }

    private static readonly ConcurrentDictionary<string, Entry> Hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test hook: the clock.</summary>
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>The names people know for the hosts the app talks to.</summary>
    public static string DisplayName(string host) => host.ToLowerInvariant() switch
    {
        "export.arxiv.org" or "arxiv.org" => "arXiv",
        "api.openalex.org" => "OpenAlex",
        "api.semanticscholar.org" => "Semantic Scholar",
        "api.crossref.org" => "Crossref",
        "api.unpaywall.org" => "Unpaywall",
        "zenodo.org" => "Zenodo",
        _ => host,
    };

    /// <summary>Records one finished request (after its retries): the status it ended with and how long it took.</summary>
    public static void Record(string host, HttpStatusCode? status, TimeSpan duration, bool timedOut = false, bool unreachable = false, int retries = 0)
    {
        var now = UtcNow();
        var entry = Hosts.GetOrAdd(host, _ => new Entry());
        lock (entry)
        {
            entry.LastDuration = duration;
            Problem problem =
                timedOut ? Problem.Timeout
                : unreachable ? Problem.Unreachable
                : status == HttpStatusCode.TooManyRequests ? Problem.RateLimited
                : status is { } s && (int)s >= 500 ? Problem.ServerError
                : retries > 0 ? Problem.RateLimited // got through, but only after waiting
                : duration > SlowAnswer ? Problem.Slow
                : Problem.None;
            bool ok = status is { } code && (int)code < 400;
            if (ok) entry.LastOkUtc = now;
            if (problem != Problem.None)
            {
                entry.LastProblem = problem;
                entry.LastProblemUtc = now;
                entry.Problems.Enqueue(now);
            }
            while (entry.Problems.Count > 0 && entry.Problems.Peek() < now - RecentWindow) entry.Problems.Dequeue();
        }
    }

    public static IReadOnlyList<HostState> Snapshot()
    {
        var now = UtcNow();
        return Hosts.Select(kv =>
        {
            lock (kv.Value)
            {
                int recent = kv.Value.Problems.Count(t => t >= now - RecentWindow);
                return new HostState(kv.Key, kv.Value.LastOkUtc, kv.Value.LastProblemUtc, kv.Value.LastProblem, kv.Value.LastDuration, recent);
            }
        }).OrderBy(h => DisplayName(h.Host), StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// One plain sentence about the databases that had trouble in the last half hour, or null when all is well
    /// (or nothing has been asked yet). Example: "arXiv is slow (answered in 18 s); Semantic Scholar is rate-limiting requests."
    /// </summary>
    public static string? Summary()
    {
        var now = UtcNow();
        var parts = Snapshot()
            .Where(h => h.LastProblemUtc is { } t && now - t <= RecentWindow && h.LastProblem != Problem.None)
            .Select(h => $"{DisplayName(h.Host)} {Describe(h)}")
            .ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private static string Describe(HostState h) => h.LastProblem switch
    {
        Problem.RateLimited => "is rate-limiting requests, so it is slower than usual",
        Problem.ServerError => "had server errors",
        Problem.Timeout => "did not answer in time",
        Problem.Unreachable => "could not be reached",
        Problem.Slow => $"is slow (answered in {Math.Round(h.LastDuration?.TotalSeconds ?? 0)} s)",
        _ => "",
    };

    /// <summary>Test hook: forget everything.</summary>
    public static void Reset() => Hosts.Clear();
}

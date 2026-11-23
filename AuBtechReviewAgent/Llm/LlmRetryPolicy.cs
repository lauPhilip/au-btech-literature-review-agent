using System;
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace AuBtechReviewAgent;

/// <summary>
/// When a failed model call is tried again, and after how long. A rate limit (429) gets more attempts than other
/// temporary errors. On 429 and 503 the server's Retry-After header is followed when it sent one; otherwise the
/// wait doubles with each attempt (2, 4, 8 s ... plus up to 0.5 s jitter). No single wait is longer than
/// <see cref="MaxWait"/>. The delay and the clock are seams, so tests never sleep.
/// </summary>
public sealed class LlmRetryPolicy
{
    /// <summary>The key in <see cref="Exception.Data"/> under which the model client puts the server's Retry-After (a TimeSpan).</summary>
    public const string RetryAfterKey = "RetryAfter";

    /// <summary>Attempts in all for a temporary error other than a rate limit (408, 5xx, network).</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Attempts in all for a rate limit (429).</summary>
    public int MaxAttemptsRateLimited { get; init; } = 6;

    /// <summary>The longest single wait, also when the server asks for longer.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait after the given failed attempt when the server did not say how long to wait.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    /// <summary>How a wait is done; tests replace it with one that only records the wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static TimeSpan DefaultBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));

    /// <summary>How long to wait before trying again after <paramref name="attempt"/> failed with <paramref name="ex"/>, or null to give up.</summary>
    public TimeSpan? NextWait(Exception ex, int attempt)
    {
        if (!RecordingChatCompletionService.IsTransient(ex)) return null;
        HttpStatusCode? status = StatusOf(ex);
        int limit = status == HttpStatusCode.TooManyRequests ? Math.Max(MaxAttempts, MaxAttemptsRateLimited) : MaxAttempts;
        if (attempt >= limit) return null;
        TimeSpan wait = status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && RetryAfterOf(ex) is { } asked
            ? asked
            : Backoff(attempt);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return wait > MaxWait ? MaxWait : wait;
    }

    /// <summary>The status code of a failed call, looked for through inner exceptions.</summary>
    public static HttpStatusCode? StatusOf(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            HttpStatusCode? status = ex switch
            {
                HttpOperationException hoe => hoe.StatusCode,
                System.Net.Http.HttpRequestException hre => hre.StatusCode,
                _ => null
            };
            if (status != null) return status;
        }
        return null;
    }

    /// <summary>The Retry-After the model client attached to a failed call, looked for through inner exceptions.</summary>
    public static TimeSpan? RetryAfterOf(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex.Data.Contains(RetryAfterKey) && ex.Data[RetryAfterKey] is TimeSpan wait) return wait;
        return null;
    }

    /// <summary>
    /// The wait a Retry-After header asks for: a number of seconds, or an HTTP date (measured from <paramref name="now"/>).
    /// Null when there is no header. A date in the past means no wait.
    /// </summary>
    public static TimeSpan? Parse(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        return null;
    }
}

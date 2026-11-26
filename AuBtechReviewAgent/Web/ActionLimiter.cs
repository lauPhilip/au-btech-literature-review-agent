using System;
using System.Collections.Generic;
using System.Threading.RateLimiting;

namespace AuBtechReviewAgent;

/// <summary>
/// Limits per client address on the actions that cost a model call or a call to another server and are started over
/// the live connection, which the HTTP rate limiter never sees: starting a review, proposing search strings,
/// re-checking a citation, the editor's check of a changed sentence, checking an uploaded archive, and adding a report to the gallery. A page's own
/// count (five proposals a page) starts again with every reload; this one does not. Counts are kept in memory per
/// address for an hour and are lost on a restart. Circuits without a known address share one count. A refusal is logged as the HTTP limiter logs it (SiteRateLimits).
/// </summary>
public sealed class ActionLimiter
{
    public const string StartReview = "start-review", ProposeStrings = "propose-strings", Recheck = "recheck",
        EditorCheck = "editor-check", VerifyUpload = "verify-upload", GallerySubmit = "gallery-submit";

    /// <summary>The defaults per address per hour; "Site:ActionsPerHour" in the configuration overrides any of them.</summary>
    public static IReadOnlyDictionary<string, int> Defaults { get; } = new Dictionary<string, int>
    {
        [StartReview] = 20,
        [ProposeStrings] = 30,
        [Recheck] = 30,
        [EditorCheck] = 60,
        [VerifyUpload] = 20,
        // Adding a report copies its files to the gallery; a few an hour is plenty for an owner.
        [GallerySubmit] = 5,
    };

    /// <summary>The one bucket for every circuit whose address is not known.</summary>
    public const string UnknownAddress = "unknown";

    private readonly Dictionary<string, int> _perHour;
    private readonly PartitionedRateLimiter<string> _limiter;

    public ActionLimiter(IReadOnlyDictionary<string, int>? perHour = null)
    {
        _perHour = new Dictionary<string, int>(Defaults, StringComparer.Ordinal);
        foreach (var (action, limit) in perHour ?? new Dictionary<string, int>()) _perHour[action] = limit;
        _limiter = PartitionedRateLimiter.Create<string, string>(partition =>
        {
            string action = partition[..partition.IndexOf('|')];
            int limit = _perHour.TryGetValue(action, out var n) ? n : 30;
            return limit <= 0
                ? RateLimitPartition.GetNoLimiter(partition)
                : RateLimitPartition.GetFixedWindowLimiter(partition,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
        });
    }

    /// <summary>How many times an hour one address may do the action; 0 means no limit.</summary>
    public int PerHour(string action) => _perHour.TryGetValue(action, out var n) ? n : 30;

    /// <summary>
    /// Counts one more use of the action for this address, or refuses it with a sentence for the page. The server
    /// itself (loopback) is not limited, as for the HTTP limiter. A page that could not learn the address passes
    /// "unknown" (or nothing); every such circuit shares one count, as in the run quota, so a lost address is never
    /// a way around the limit.
    /// </summary>
    public bool TryTake(string action, string? clientAddress, out string refusal)
    {
        refusal = "";
        if (string.IsNullOrWhiteSpace(clientAddress)) clientAddress = UnknownAddress;
        if (clientAddress is "127.0.0.1" or "::1") return true;
        using var lease = _limiter.AttemptAcquire(action + "|" + clientAddress);
        if (lease.IsAcquired) return true;
        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromHours(1);
        refusal = $"This was done many times from your network address in the last hour. Try again {RunQuotaService.ResetText(wait)}.";
        SiteRateLimits.LogRefusal(action, clientAddress);
        return false;
    }
}

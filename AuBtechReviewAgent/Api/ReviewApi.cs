using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// What the REST API and the MCP server do, in one place: start a systematic review with the caller's own keys,
/// and read where it stands. Both doors call these methods, so they share the input guard, the run slots, the
/// queue and the limits with each other and with the New review form.
///
/// The caller's Mistral key goes into the run's <see cref="ReviewRequest"/> and nowhere else. The engine resolves
/// it into the chat service and never writes the request's keys (the protocol is written with UserKeys = null).
/// There are no free reviews here: without the caller's own key nothing starts, and the server's key is never a
/// fallback, because a request with a key never falls back to it.
/// </summary>
public sealed partial class ReviewApi
{
    public const int DefaultMaxResults = 5;

    private readonly PrismaReviewEngine _engine;
    private readonly RunQuotaService _quota;
    private readonly ApiKeyFingerprint _fingerprint;
    private readonly PartitionedRateLimiter<string>? _starts;

    private static ILogger Log => AppLog.For<ReviewApi>();

    public ReviewApi(PrismaReviewEngine engine, RunQuotaService quota, ApiKeyFingerprint fingerprint)
    {
        _engine = engine;
        _quota = quota;
        _fingerprint = fingerprint;
        // Starting a review is what costs: per key and per address, as many runs an hour as the form allows
        // with an own key (Quota:OwnKeyRunsPerHour; 0 means no limit there, and none here).
        int perHour = quota.Options.OwnKeyRunsPerHour;
        if (perHour > 0)
        {
            _starts = PartitionedRateLimiter.Create<string, string>(partition => RateLimitPartition.GetFixedWindowLimiter(partition,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        }
    }

    /// <summary>Checks the request as the form does, then starts the run and answers at once.</summary>
    public ApiResult<ReviewStarted> Start(StartReviewBody? body, ApiCallerKeys? keys, string clientAddress, string baseUrl)
    {
        if (keys == null || keys.Mistral.Length == 0) return ApiProblems.MissingKey();
        if (_engine.Llm.IsOpenAICompatible) return ApiProblems.NoMistral();
        if (body == null) return ApiProblems.Invalid("The request has no body. Send the review's fields as JSON.", Array.Empty<string>());

        var userKeys = keys.ToUserKeys();
        var problems = new List<string>();
        string kind = string.IsNullOrWhiteSpace(body.VisualSummary) ? ArtifactKinds.ConceptMap : body.VisualSummary.Trim();
        if (!ArtifactKinds.All.Any(k => k.Key == kind))
            problems.Add($"Visual summary \"{Clip(kind)}\" is not one of: {string.Join(", ", ArtifactKinds.All.Select(k => k.Key))}.");
        var sources = PickSources(body.Databases, userKeys, problems);
        int maxYear = DateTime.UtcNow.Year + 1;
        foreach (var (name, year) in new[] { ("Year from", body.YearFrom), ("Year to", body.YearTo) })
        {
            if (year != 0 && (year < 1800 || year > maxYear)) problems.Add($"{name} is {year}; use 0 for no limit, or a year from 1800 to {maxYear}.");
        }

        var request = new ReviewRequest(body.SearchWords ?? "", body.Objective ?? "", body.Inclusion ?? "", body.Exclusion ?? "",
            _quota.MaxResultsFor(QuotaTier.OwnKey, body.MaxResultsPerSource ?? DefaultMaxResults),
            body.PeerReviewOnly, body.VisualSummaryNote ?? "", userKeys, sources, body.YearFrom, body.YearTo,
            HumanScreeningReview: false, DualScreening: body.DualScreening ?? true, CitationChaining: body.CitationChaining,
            ArtifactKind: kind, SearchStrings: body.SearchStrings, Method: ReviewMethod.SystematicKey,
            ResearchQuestions: body.ResearchQuestions);
        var check = ReviewInputGuard.Check(request);
        problems.AddRange(check.Errors);
        if (problems.Count > 0) return ApiProblems.Invalid("Some fields need a change before the review can start.", problems);
        if (check.Flags.Count > 0 && !body.AcceptFlaggedInput) return ApiProblems.Flagged(check.Flags.Select(f => f.ToString()).ToList());

        // The same line as the form: a full line, or a client with its share, is refused before a start is counted.
        var runId = Guid.NewGuid();
        var admission = _engine.Coordinator.TryAdmit(runId, clientAddress);
        if (!admission.Admitted) return ApiProblems.Busy(admission);
        if (!TryTakeStart(keys.Mistral, clientAddress, out var wait))
        {
            _engine.Coordinator.Withdraw(runId);
            return ApiProblems.TooMany(wait);
        }

        string editKey = _engine.ClaimRun(runId);
        var engine = _engine;
        // The run must not carry this request's context (its HttpContext, its logging scope) for the minutes it takes.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try { await engine.RunReviewAsync(runId, request); }
                catch (Exception ex)
                {
                    // The reason is in the run's ledger, and the engine logged it with the run id; only the type here.
                    Log.LogDebug("API run {RunId} ended with {Error}", runId, ex.GetType().Name);
                }
            });
        }
        Log.LogInformation("API run {RunId} started", runId);
        return ApiResult<ReviewStarted>.Ok(new ReviewStarted(runId, editKey, PrismaReviewEngine.StageQueued, LinksFor(runId, baseUrl)));
    }

    /// <summary>The databases asked for, checked against the catalogue; none asked for means every one the keys reach.</summary>
    private List<string> PickSources(List<string>? asked, UserApiKeys keys, List<string> problems)
    {
        if (asked == null || asked.Count == 0)
            return _engine.GetSourceAvailability(keys).Where(s => s.Value).Select(s => s.Key).ToList();
        var picked = new List<string>();
        foreach (string name in asked.Take(SourceCatalog.All.Count + 1))
        {
            if (SourceCatalog.Find(name ?? "") is { } source) { if (!picked.Contains(source.Key)) picked.Add(source.Key); }
            else problems.Add($"Database \"{Clip(name ?? "")}\" is not one of: {string.Join(", ", SourceCatalog.AllKeys)}.");
        }
        return picked;
    }

    /// <summary>One start more for this key and this address, or false with how long to wait.</summary>
    private bool TryTakeStart(string mistralKey, string clientAddress, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (_starts == null) return true;
        foreach (string partition in new[] { "key:" + _fingerprint.Of(mistralKey), "address:" + clientAddress })
        {
            using var lease = _starts.AttemptAcquire(partition);
            if (lease.IsAcquired) continue;
            wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromHours(1);
            return false;
        }
        return true;
    }

    internal static ReviewLinks LinksFor(Guid runId, string baseUrl) => new(
        $"{baseUrl}/api/v1/reviews/{runId}",
        $"{baseUrl}/review/{runId}");

    /// <summary>A value the caller sent, cut short for an error message.</summary>
    internal static string Clip(string value) => value.Length <= 40 ? value : value[..40] + "…";
}

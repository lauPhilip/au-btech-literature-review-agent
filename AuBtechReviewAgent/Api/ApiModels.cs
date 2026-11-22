using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>The body of POST /api/v1/reviews: the fields of the New review form.</summary>
public sealed class StartReviewBody
{
    /// <summary>What the review should find out (optional, as on the form).</summary>
    public string? Objective { get; init; }

    /// <summary>The search words, the primary query (required).</summary>
    public string? SearchWords { get; init; }

    /// <summary>Up to four extra search strings. Left out, the model proposes them during the run, as on the form.</summary>
    public List<string>? SearchStrings { get; init; }

    /// <summary>What a study must be about to be included (required).</summary>
    public string? Inclusion { get; init; }

    public string? Exclusion { get; init; }

    /// <summary>Publication years; 0 means no limit.</summary>
    public int YearFrom { get; init; }
    public int YearTo { get; init; }

    /// <summary>Database keys (SourceCatalog). Left out: every database the caller's keys can reach.</summary>
    public List<string>? Databases { get; init; }

    /// <summary>Up to four research questions, answered one by one in the conclusion.</summary>
    public List<string>? ResearchQuestions { get; init; }

    /// <summary>The visual summary: an ArtifactKinds key such as "concept-map", "table" or "none".</summary>
    public string? VisualSummary { get; init; }

    /// <summary>What the visual summary should show (the form's line under the choice).</summary>
    public string? VisualSummaryNote { get; init; }

    /// <summary>Results per database, 1 to 50 (default 5, as on the form).</summary>
    public int? MaxResultsPerSource { get; init; }

    public bool PeerReviewOnly { get; init; }
    public bool? DualScreening { get; init; }
    public bool CitationChaining { get; init; }

    /// <summary>
    /// The form asks once before a run whose fields contain instruction-like phrases. Through the API the caller
    /// confirms with true; the phrases are then written to the protocol, as with the form.
    /// </summary>
    public bool AcceptFlaggedInput { get; init; }
}

/// <summary>The caller's own keys, from the request headers. They live only as long as the run that uses them.</summary>
public sealed record ApiCallerKeys(string Mistral, string Elsevier = "", string Ieee = "", string Scholar = "")
{
    public UserApiKeys ToUserKeys() => new(Mistral,
        Elsevier.Length > 0 ? Elsevier : null, Ieee.Length > 0 ? Ieee : null, Scholar.Length > 0 ? Scholar : null);

    // A record prints its members; this one must never print a key, in a log line or anywhere else.
    public override string ToString() => "ApiCallerKeys { (hidden) }";
}

/// <summary>An answer of the API: a value, or a problem to send as RFC 9457 problem details.</summary>
public sealed record ApiResult<T>(T? Value, ApiProblem? Problem)
{
    public static ApiResult<T> Ok(T value) => new(value, null);
    public static implicit operator ApiResult<T>(ApiProblem problem) => new(default, problem);
}

/// <summary>What went wrong, in plain English, with the HTTP status that goes with it.</summary>
public sealed record ApiProblem(int Status, string Title, string Detail, IReadOnlyDictionary<string, object?>? Extensions = null);

/// <summary>The answer to a started review: its id, its edit key and where to follow it.</summary>
public sealed record ReviewStarted(Guid Id, string EditKey, string Stage, ReviewLinks Links);

/// <summary>
/// The answer to a cancel: the run was asked to stop, with this reason. It ends in the stage "Cancelled" at its next
/// safe point; follow its status to see when.
/// </summary>
public sealed record ReviewCancelled(Guid Id, string Reason, ReviewLinks Links);

/// <summary>Addresses that always exist for a run: its status and its page on the site.</summary>
public sealed record ReviewLinks(string Status, string Page);

/// <summary>Addresses of a finished run's files.</summary>
public sealed record ReviewDownloads(string Archive, string BibTex, string Ris, string ScreenedRis, string Protocol);

public sealed record ReviewCounts(int Identified, int Screened, int Excluded, int Included);

/// <summary>How the citation checks came out, as on the run's summary card.</summary>
public sealed record ReviewVerdicts(int Checked, int Supported, int PartlySupported, int NotSupported, int Unverifiable);

/// <summary>
/// Where a review stands. Step counts the seven steps the run view shows (ReviewSteps): 0 while the run waits for
/// a slot, 1 to 7 while it works, and 7 when it is done.
/// </summary>
public sealed record ReviewStatus(
    Guid Id,
    string Stage,
    int Step,
    int Steps,
    string StepName,
    int Percent,
    string Detail,
    int QueuePosition,
    ReviewCounts Counts,
    ReviewVerdicts? Verdicts,
    string? Failure,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    ReviewLinks Links,
    ReviewDownloads? Downloads);

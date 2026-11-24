using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>Every problem the API can answer with, in plain English (sent as RFC 9457 problem details).</summary>
public static class ApiProblems
{
    public static ApiProblem MissingKey() => new(401, "Your own Mistral key is missing",
        "Send your own Mistral API key in the X-Mistral-Key header. The API never uses the server's key, and it does not keep yours.");

    public static ApiProblem NoMistral() => new(503, "The API needs Mistral",
        "This server runs its own model, so a review through the API could not use your key. Start the review on the website instead.");

    public static ApiProblem Invalid(string detail, IReadOnlyList<string> errors) => new(400, "The review cannot start", detail,
        new Dictionary<string, object?> { ["errors"] = errors });

    public static ApiProblem Flagged(IReadOnlyList<string> flags) => new(422, "Some fields read like instructions to the model",
        "The fields are only ever used as data, but the form asks once before such a run. Send \"acceptFlaggedInput\": true to start it anyway; the phrases are then written to the protocol.",
        new Dictionary<string, object?> { ["flags"] = flags });

    public static ApiProblem TooMany(TimeSpan wait) => new(429, "Too many reviews started",
        $"This key or this address has started as many reviews as it may this hour. Try again {RunQuotaService.ResetText(wait)}.",
        new Dictionary<string, object?> { ["retryAfterSeconds"] = (int)Math.Ceiling(wait.TotalSeconds) });

    /// <summary>The line is full (503), or this address has its share of runs running and waiting (429).</summary>
    public static ApiProblem Busy(RunAdmission refusal) => refusal.Busy
        ? new(503, "The tool is busy", refusal.Refusal ?? "Too many reviews are waiting. Try again later.",
            new Dictionary<string, object?> { ["retryAfterSeconds"] = (int)Math.Ceiling((refusal.RetryAfter ?? TimeSpan.FromMinutes(15)).TotalSeconds) })
        : new(429, "Too many reviews waiting", refusal.Refusal ?? "This address has as many reviews running or waiting as it may.");

    public static ApiProblem TooManyCalls() => new(429, "Too many calls",
        "Slow down a little. This key or this address has made too many calls in the last minute.");

    public static ApiProblem NotReady(string stage) => new(409, "The report is not ready",
        PrismaReviewEngine.IsFinishedStage(stage)
            ? $"The review ended as \"{stage}\" without a report. Its status says why."
            : "The review is still running. Follow its status, and ask again when the stage is \"Complete\".",
        new Dictionary<string, object?> { ["stage"] = stage });

    public static ApiProblem MissingEditKey() => new(401, "The edit key is missing",
        "Send the review's edit key in the X-Edit-Key header. It came back with the review when it started.");

    public static ApiProblem WrongEditKey() => new(403, "The edit key does not fit",
        "Only the edit key that came back when this review started can cancel it.");

    public static ApiProblem NotCancellable(string stage) => new(409, "The review is not running",
        PrismaReviewEngine.IsFinishedStage(stage)
            ? $"The review has already ended as \"{stage}\", so there is nothing to cancel."
            : "The review is not running on this server, or it is being cancelled already. Its status shows where it stands.",
        new Dictionary<string, object?> { ["stage"] = stage });

    public static ApiProblem NoRun() => new(404, "No review with this id",
        "There is no review with this id. Runs are deleted some days after they finish.");

    public static ApiProblem NotSystematic() => new(404, "Not a systematic review",
        "This run is another kind of review. The API serves systematic reviews only.");
}

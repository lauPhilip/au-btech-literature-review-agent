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

    public static ApiProblem TooManyCalls() => new(429, "Too many calls",
        "Slow down a little. This key or this address has made too many calls in the last minute.");

    public static ApiProblem NoRun() => new(404, "No review with this id",
        "There is no review with this id. Runs are deleted some days after they finish.");

    public static ApiProblem NotSystematic() => new(404, "Not a systematic review",
        "This run is another kind of review. The API serves systematic reviews only.");
}

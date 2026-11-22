using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Cancelling a run (issue #233). The owner of a run, the holder of its edit key, may stop it. A queued run leaves
/// the line, a run waiting for the human screening check stops waiting, and a running one stops at its next safe
/// point: before its next model call or its next step. The run ends in the stage "Cancelled", with the time and the
/// reason in its ledger and in run.json, and its folder is kept until the normal clean-up.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>The longest reason kept on file; a longer one is cut.</summary>
    public const int MaxCancelReasonLength = 300;

    public const string NoCancelReason = "No reason was given.";

    private readonly List<Func<Guid, string?, string, bool>> _otherCancellers = new();

    /// <summary>
    /// Lets the engine cancel the runs of another module, such as the grey literature runner, so callers need only
    /// <see cref="CancelRun"/>. The function gets the run id, the edit key and the cleaned reason.
    /// </summary>
    public void AddCanceller(Func<Guid, string?, string, bool> cancel)
    {
        lock (_otherCancellers) _otherCancellers.Add(cancel);
    }

    /// <summary>
    /// The one way to cancel a run, for the page and for a later API. True when a live run was asked to stop;
    /// false when the key does not fit, or the run is finished, unknown or cancelled already.
    /// </summary>
    public bool CancelRun(Guid runId, string? editKey, string? reason)
    {
        if (!CanEdit(runId, editKey)) return false;
        string clean = CleanCancelReason(reason);
        if (Coordinator.Cancel(runId, clean))
        {
            _log.LogInformation("Run {RunId}: its owner asked to cancel it.", runId);
            return true;
        }
        List<Func<Guid, string?, string, bool>> others;
        lock (_otherCancellers) others = _otherCancellers.ToList();
        return others.Any(cancel => cancel(runId, editKey, clean));
    }

    /// <summary>The reason as it is kept: one line of plain text, at most <see cref="MaxCancelReasonLength"/> characters.</summary>
    public static string CleanCancelReason(string? reason)
    {
        string clean = ReviewInputGuard.Normalize(reason, singleLine: true).Trim();
        if (clean.Length > MaxCancelReasonLength) clean = clean[..MaxCancelReasonLength].TrimEnd();
        return clean.Length == 0 ? NoCancelReason : clean;
    }

    /// <summary>Writes the end of a cancelled run to its ledger: the stage, when it was cancelled, and why.</summary>
    private async Task EndCancelledAsync(RunContext ctx)
    {
        var request = Coordinator.CancelRequest(ctx.RunId);
        _log.LogInformation("Run {RunId} was cancelled during {Stage}.", ctx.RunId, ctx.State.Stats.ProcessingStage);
        ctx.State.Stats.ProcessingStage = StageCancelled;
        ctx.State.Stats.QueuePosition = 0;
        ctx.State.CancelledUtc = request?.RequestedUtc ?? DateTime.UtcNow;
        ctx.State.CancelReason = request?.Reason ?? NoCancelReason;
        ctx.State.FailureMessage = $"The run was cancelled by the person who started it. Reason: {ctx.State.CancelReason}";
        ctx.State.CompletedUtc = DateTime.UtcNow;
        try { await PublishAsync(ctx); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning("Could not save the ledger of the cancelled run: {Message}", SanitizeLogMessage(ex.Message));
        }
    }
}

/// <summary>
/// Thrown by <see cref="PrismaReviewEngine.RunReviewAsync"/> when the run's owner cancelled it, and only then, so a
/// caller can tell a cancelled run from one that failed (a timeout is an OperationCanceledException too).
/// </summary>
public sealed class RunCancelledException : OperationCanceledException
{
    public RunCancelledException(Exception inner, CancellationToken token)
        : base("The run was cancelled by the person who started it.", inner, token) { }
}

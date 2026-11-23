using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Cancelling a grey literature run (issue #233). Each step writes its files and moves the stage on only when it is
/// done, so the safe point is between two steps: the step that is running finishes, and the run stops before the next
/// one. The stage in run.json stays at that step, so nothing half-written is read as finished and starting the run
/// again continues from there. The runner's status ends as "Cancelled", and run.json keeps when and why.
/// </summary>
public sealed partial class MultivocalRunner
{
    private sealed record StopRequest(DateTime? StartedUtc, DateTime RequestedUtc, string Reason);

    private readonly ConcurrentDictionary<Guid, StopRequest> _stops = new();

    /// <summary>How many runs are running on this server now, for /health: the deploy waits until no run is going.</summary>
    public int ActiveRunCount => _status.Values.Count(s => s.Running);

    /// <summary>
    /// Asks a running run to stop before its next step (only with the run's edit key). False when the key does not
    /// fit, the run is not running, or this start of it has been cancelled already.
    /// </summary>
    public bool Cancel(Guid runId, string? editKey, string reason)
    {
        if (!_runs.CanEdit(runId, editKey) || StatusOf(runId) is not { Running: true } status) return false;
        var request = new StopRequest(status.StartedUtc, DateTime.UtcNow, PrismaReviewEngine.CleanCancelReason(reason));
        while (true)
        {
            if (_stops.TryGetValue(runId, out var earlier))
            {
                if (earlier.StartedUtc == status.StartedUtc) return false;
                if (_stops.TryUpdate(runId, request, earlier)) break;
            }
            else if (_stops.TryAdd(runId, request)) break;
        }
        StopWaiting(runId);
        return true;
    }

    /// <summary>At the safe point before a step: true when this start of the run was cancelled, after recording it.</summary>
    private async Task<bool> StoppedAsync(Guid runId)
    {
        // A request left over from an earlier start of the run (it ended before it reached a safe point) does not count.
        if (!_stops.TryGetValue(runId, out var stop) || StatusOf(runId)?.StartedUtc != stop.StartedUtc) return false;
        _stops.TryRemove(runId, out _);
        if (_runs.LoadHeader(runId) is { } header)
            await RunHeader.WriteAsync(_runs.FolderOf(runId), header with { CancelledUtc = stop.RequestedUtc, CancelReason = stop.Reason });
        Set(runId, new MultivocalRunStatus(false, PrismaReviewEngine.StageCancelled, null,
            $"The run was cancelled by the person who started it. Reason: {stop.Reason} Start it again to continue from this step."));
        return true;
    }
}

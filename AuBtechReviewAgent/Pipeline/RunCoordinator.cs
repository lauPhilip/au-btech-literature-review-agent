using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Runs" section of appsettings.json.</summary>
public class RunsOptions
{
    /// <summary>How many reviews may call the language model at the same time. Others wait in line.</summary>
    public int MaxConcurrentRuns { get; set; } = 3;

    /// <summary>The most slots one client (network address) may hold at once; its next run lets others pass.</summary>
    public int MaxRunningPerClient { get; set; } = 2;

    /// <summary>The most runs one client may have waiting on top of those running; more are refused.</summary>
    public int MaxQueuedPerClient { get; set; } = 3;

    /// <summary>The longest the line may grow. A run beyond it is refused with "The tool is busy" instead of waiting for hours.</summary>
    public int MaxQueuedRuns { get; set; } = 100;

    /// <summary>How long a run holds a slot, for the wait shown in line until a few runs have been timed.</summary>
    public int TypicalRunMinutes { get; set; } = 12;

    /// <summary>How long a finished run's folder (and its link) is kept before the cleanup job deletes it.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>How long a run waits for the user to confirm screening decisions before it continues with the model's decisions.</summary>
    public int ScreeningReviewTimeoutHours { get; set; } = 24;

    /// <summary>
    /// The id of a finished run shown as "See an example report" on the landing page. That run is never deleted by
    /// the clean-up job and can be read by anyone but changed by no one. Empty: no example is shown.
    /// </summary>
    public string DemoRunId { get; set; } = "";
}

/// <summary>A human reviewer's decision on one screened record.</summary>
public record ScreeningOverride(string PaperId, string Decision, string? Note);

/// <summary>
/// Keeps track of the runs that are alive in this process and hands out a limited number of "slots" for
/// the model-heavy phases, through one fair line (RunCoordinator.Queue.cs). Everything here is in memory: after an
/// app restart no run is active, and PrismaReviewEngine.MarkInterruptedRuns tidies up the ledgers that were cut off.
/// </summary>
public partial class RunCoordinator
{
    private readonly int _maxConcurrent;
    private readonly RunsOptions _options;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IReadOnlyList<ScreeningOverride>>> _reviewGates = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _active = new();
    private readonly ConcurrentDictionary<Guid, RunStop> _stops = new();

    public RunCoordinator(int maxConcurrent) : this(new RunsOptions { MaxConcurrentRuns = maxConcurrent }) { }

    public RunCoordinator(RunsOptions options)
    {
        _options = options;
        _maxConcurrent = Math.Max(1, options.MaxConcurrentRuns);
    }

    public int MaxConcurrent => _maxConcurrent;

    /// <summary>Marks the run as alive; the token is cancelled when the run's owner cancels it (<see cref="Cancel"/>).</summary>
    public CancellationToken Register(Guid runId)
    {
        _active[runId] = DateTime.UtcNow;
        var stop = new RunStop();
        _stops[runId] = stop;
        return stop.Source.Token;
    }

    public void Unregister(Guid runId)
    {
        _active.TryRemove(runId, out _);
        _reviewGates.TryRemove(runId, out _);
        _stops.TryRemove(runId, out _);
        Withdraw(runId);
    }

    /// <summary>
    /// Asks a live run to stop at its next safe point: a queued run leaves the line at once, a waiting one stops
    /// waiting, a running one stops before its next model call or step. False when the run is not alive here, has
    /// been cancelled already, or is past its last safe point (<see cref="Close"/>).
    /// </summary>
    public bool Cancel(Guid runId, string reason)
    {
        if (!_stops.TryGetValue(runId, out var stop)) return false;
        lock (stop)
        {
            if (stop.Closed || stop.Reason != null) return false;
            stop.Reason = reason;
            stop.RequestedUtc = DateTime.UtcNow;
            stop.Source.Cancel();
            return true;
        }
    }

    /// <summary>When the run was cancelled and why; null when it was not.</summary>
    public (DateTime RequestedUtc, string Reason)? CancelRequest(Guid runId)
    {
        if (!_stops.TryGetValue(runId, out var stop)) return null;
        lock (stop) return stop.Reason == null ? null : (stop.RequestedUtc, stop.Reason);
    }

    /// <summary>Called after a run's last safe point: from here on it finishes, and a cancel no longer counts.</summary>
    public void Close(Guid runId)
    {
        if (!_stops.TryGetValue(runId, out var stop)) return;
        lock (stop) stop.Closed = true;
    }

    /// <summary>True while the run is queued, running or waiting for a screening review in this process.</summary>
    public bool IsActive(Guid runId) => _active.ContainsKey(runId);

    public IReadOnlyCollection<Guid> ActiveRunIds => _active.Keys.ToList();

    /// <summary>Opens the review gate so the dashboard can submit decisions (call before announcing the pause).</summary>
    public void OpenScreeningReview(Guid runId) =>
        _reviewGates.GetOrAdd(runId, _ => new TaskCompletionSource<IReadOnlyList<ScreeningOverride>>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>
    /// Called by the engine: waits until the user submits their screening review, or the timeout passes (returns null).
    /// Throws <see cref="OperationCanceledException"/> when the run is cancelled while it waits.
    /// </summary>
    public async Task<IReadOnlyList<ScreeningOverride>?> WaitForScreeningReviewAsync(Guid runId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var gate = _reviewGates.GetOrAdd(runId, _ => new TaskCompletionSource<IReadOnlyList<ScreeningOverride>>(TaskCreationOptions.RunContinuationsAsynchronously));
        var finished = await Task.WhenAny(gate.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        _reviewGates.TryRemove(runId, out _);
        if (finished != gate.Task) cancellationToken.ThrowIfCancellationRequested();
        return finished == gate.Task ? gate.Task.Result : null;
    }

    public bool IsAwaitingScreeningReview(Guid runId) => _reviewGates.ContainsKey(runId);

    /// <summary>Called by the dashboard. Returns false if the run is not (or no longer) waiting for a review.</summary>
    public bool SubmitScreeningReview(Guid runId, IReadOnlyList<ScreeningOverride> decisions) =>
        _reviewGates.TryGetValue(runId, out var gate) && gate.TrySetResult(decisions);

    // A CancellationTokenSource without a timer holds nothing that needs disposing, so it is simply dropped.
    private sealed class RunStop
    {
        public CancellationTokenSource Source { get; } = new();
        public string? Reason { get; set; }
        public DateTime RequestedUtc { get; set; }
        public bool Closed { get; set; }
    }
}

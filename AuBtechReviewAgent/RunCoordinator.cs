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

    /// <summary>How long a finished run's folder (and its link) is kept before the cleanup job deletes it.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>How long a run waits for the user to confirm screening decisions before it continues with the model's decisions.</summary>
    public int ScreeningReviewTimeoutHours { get; set; } = 24;
}

/// <summary>A human reviewer's decision on one screened record.</summary>
public record ScreeningOverride(string PaperId, string Decision, string? Note);

/// <summary>
/// Keeps track of the runs that are alive in this process and hands out a limited number of "slots" for
/// the model-heavy phases, first come first served. Everything here is in memory: after an app restart
/// no run is active, and PrismaReviewEngine.MarkInterruptedRuns tidies up the ledgers that were cut off.
/// </summary>
public class RunCoordinator
{
    private readonly int _maxConcurrent;
    private readonly object _gate = new();
    private int _running;
    private readonly LinkedList<(Guid RunId, TaskCompletionSource Ticket)> _queue = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IReadOnlyList<ScreeningOverride>>> _reviewGates = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _active = new();

    public RunCoordinator(int maxConcurrent)
    {
        _maxConcurrent = Math.Max(1, maxConcurrent);
    }

    public int MaxConcurrent => _maxConcurrent;

    public void Register(Guid runId) => _active[runId] = DateTime.UtcNow;
    public void Unregister(Guid runId)
    {
        _active.TryRemove(runId, out _);
        _reviewGates.TryRemove(runId, out _);
    }

    /// <summary>True while the run is queued, running or waiting for a screening review in this process.</summary>
    public bool IsActive(Guid runId) => _active.ContainsKey(runId);

    public IReadOnlyCollection<Guid> ActiveRunIds => _active.Keys.ToList();

    /// <summary>1-based place in the waiting line, or 0 when the run is not waiting.</summary>
    public int QueuePosition(Guid runId)
    {
        lock (_gate)
        {
            int i = 1;
            foreach (var entry in _queue)
            {
                if (entry.RunId == runId) return i;
                i++;
            }
            return 0;
        }
    }

    /// <summary>
    /// Waits for a free slot. The returned handle must be disposed to give the slot back. Waiting runs are
    /// served strictly in arrival order (SemaphoreSlim does not promise that).
    /// </summary>
    public async Task<IDisposable> AcquireSlotAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource ticket;
        LinkedListNode<(Guid, TaskCompletionSource)> node;
        lock (_gate)
        {
            if (_running < _maxConcurrent && _queue.Count == 0)
            {
                _running++;
                return new Slot(this);
            }
            ticket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _queue.AddLast((runId, ticket));
        }

        using (cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (node.List != null) { _queue.Remove(node); ticket.TrySetCanceled(); }
            }
        }))
        {
            await ticket.Task.ConfigureAwait(false);
        }
        return new Slot(this); // the slot was handed over by Release, so _running already counts it
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_queue.First is { } next)
            {
                _queue.RemoveFirst();
                next.Value.Ticket.TrySetResult(); // hand the slot straight to the next run in line
            }
            else
            {
                _running = Math.Max(0, _running - 1);
            }
        }
    }

    /// <summary>Opens the review gate so the dashboard can submit decisions (call before announcing the pause).</summary>
    public void OpenScreeningReview(Guid runId) =>
        _reviewGates.GetOrAdd(runId, _ => new TaskCompletionSource<IReadOnlyList<ScreeningOverride>>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Called by the engine: waits until the user submits their screening review, or the timeout passes (returns null).</summary>
    public async Task<IReadOnlyList<ScreeningOverride>?> WaitForScreeningReviewAsync(Guid runId, TimeSpan timeout)
    {
        var gate = _reviewGates.GetOrAdd(runId, _ => new TaskCompletionSource<IReadOnlyList<ScreeningOverride>>(TaskCreationOptions.RunContinuationsAsynchronously));
        var finished = await Task.WhenAny(gate.Task, Task.Delay(timeout)).ConfigureAwait(false);
        _reviewGates.TryRemove(runId, out _);
        return finished == gate.Task ? gate.Task.Result : null;
    }

    public bool IsAwaitingScreeningReview(Guid runId) => _reviewGates.ContainsKey(runId);

    /// <summary>Called by the dashboard. Returns false if the run is not (or no longer) waiting for a review.</summary>
    public bool SubmitScreeningReview(Guid runId, IReadOnlyList<ScreeningOverride> decisions) =>
        _reviewGates.TryGetValue(runId, out var gate) && gate.TrySetResult(decisions);

    private sealed class Slot : IDisposable
    {
        private RunCoordinator? _owner;
        public Slot(RunCoordinator owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

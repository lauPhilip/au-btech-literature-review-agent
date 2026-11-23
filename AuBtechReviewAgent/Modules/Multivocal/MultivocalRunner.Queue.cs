using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// A multivocal run waits in the same line as every other review (<see cref="RunCoordinator"/>) and holds one slot
/// for all its steps. While it waits, its status is "Waiting in line" with its place and a rough wait, and its owner
/// can leave the line by cancelling it. Without a coordinator (the tests that call the runner directly) it starts at once.
/// </summary>
public sealed partial class MultivocalRunner
{
    public const string StepWaiting = "Waiting in line";

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _waits = new();

    /// <summary>The line every review shares; null runs the steps without waiting.</summary>
    public RunCoordinator? Coordinator { get; init; }

    /// <summary>Joins the line; when it is refused, the status says why and the run is not started.</summary>
    private bool Admit(Guid runId, string? client)
    {
        if (Coordinator == null) return true;
        var admission = Coordinator.TryAdmit(runId, client);
        if (admission.Admitted) return true;
        Set(runId, new MultivocalRunStatus(false, "Not started", null, admission.Refusal));
        return false;
    }

    private async Task RunInLineAsync(Guid runId, string? editKey)
    {
        if (Coordinator == null)
        {
            await RunStepsAsync(runId, editKey);
            return;
        }
        IDisposable? slot = null;
        var wait = new CancellationTokenSource();
        _waits[runId] = wait;
        try
        {
            try
            {
                slot = await Coordinator.AcquireSlotAsync(runId, wait.Token, place =>
                {
                    Set(runId, new MultivocalRunStatus(true, StepWaiting, place.Text, null));
                    return Task.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                // The owner left the line: the run ends as cancelled, as it would between two steps.
                if (!await StoppedAsync(runId)) Set(runId, new MultivocalRunStatus(false, StepWaiting, null, "The run left the line before it started."));
                return;
            }
            finally
            {
                _waits.TryRemove(runId, out _);
                wait.Dispose();
            }
            await RunStepsAsync(runId, editKey);
        }
        finally
        {
            slot?.Dispose();
            Coordinator.Withdraw(runId);
        }
    }

    /// <summary>A run that is cancelled while it waits leaves the line at once instead of at its first step.</summary>
    private void StopWaiting(Guid runId)
    {
        if (!_waits.TryGetValue(runId, out var wait)) return;
        try { wait.Cancel(); }
        catch (ObjectDisposedException) { /* the run got its slot in the meantime; it stops before its first step */ }
    }
}

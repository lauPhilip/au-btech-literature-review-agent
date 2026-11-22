namespace AuBtechReviewAgent.Components.Pages;

// Cancelling the run on the page (issue #197). Each state names the run it belongs to, so a new run on the same
// page starts with a fresh card.
public partial class Home
{
    internal Guid confirmCancelFor;
    internal Guid cancelRequestedFor;
    internal Guid cancelProblemFor;
    internal string cancelReason = "";

    /// <summary>The card shows only to the browser with the edit key, and only while the run is alive here.</summary>
    internal bool CanCancel => !Composing && canEdit && currentSessionId != Guid.Empty && ReviewEngine.IsRunActive(currentSessionId);

    internal bool ConfirmingCancel => confirmCancelFor == currentSessionId && currentSessionId != Guid.Empty;
    internal bool CancelRequested => cancelRequestedFor == currentSessionId && currentSessionId != Guid.Empty;

    internal string? CancelProblem => cancelProblemFor == currentSessionId && currentSessionId != Guid.Empty
        ? "The review could not be cancelled. It may have just finished." : null;

    internal void AskToCancel()
    {
        confirmCancelFor = currentSessionId;
        cancelReason = "";
    }

    internal void KeepRunning() => confirmCancelFor = Guid.Empty;

    internal void SetCancelReason(string reason) => cancelReason = reason;

    /// <summary>Cancels the run through the engine, which checks the edit key again on the server.</summary>
    internal void CancelReview()
    {
        var runId = currentSessionId;
        confirmCancelFor = Guid.Empty;
        if (!CanCancel) return;
        if (ReviewEngine.CancelRun(runId, editKey, cancelReason)) cancelRequestedFor = runId;
        else cancelProblemFor = runId;
        cancelReason = "";
    }
}

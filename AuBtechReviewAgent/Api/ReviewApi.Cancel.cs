using System;

namespace AuBtechReviewAgent;

// Cancelling a review (issue #232): the same engine call as the Cancel button on the run's page, with the edit key
// that the start returned. The engine checks the key again, and writes the time and the reason to the ledger.
public sealed partial class ReviewApi
{
    /// <summary>
    /// Asks a queued or running review to stop. It leaves the line at once, or stops at its next safe point, and
    /// ends in the stage "Cancelled"; its status shows when it has.
    /// </summary>
    public ApiResult<ReviewCancelled> Cancel(Guid runId, string? editKey, string? reason, string baseUrl)
    {
        var header = _engine.LoadHeader(runId);
        if (header == null) return ApiProblems.NoRun();
        if (header.Module != ReviewMethod.SystematicKey) return ApiProblems.NotSystematic();
        if (string.IsNullOrWhiteSpace(editKey)) return ApiProblems.MissingEditKey();
        if (!_engine.CanEdit(runId, editKey)) return ApiProblems.WrongEditKey();

        if (!_engine.IsRunActive(runId) || !_engine.CancelRun(runId, editKey, reason))
        {
            string stage = _engine.LiveStats(runId)?.ProcessingStage ?? _engine.LoadState(runId)?.Stats.ProcessingStage ?? "";
            return ApiProblems.NotCancellable(stage);
        }
        return ApiResult<ReviewCancelled>.Ok(new ReviewCancelled(runId, PrismaReviewEngine.CleanCancelReason(reason), LinksFor(runId, baseUrl)));
    }
}

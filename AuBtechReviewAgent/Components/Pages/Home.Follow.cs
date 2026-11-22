using Microsoft.AspNetCore.Components;

namespace AuBtechReviewAgent.Components.Pages;

// Following a run: reading its state file, subscribing to progress, and the human screening check.
public partial class Home
{
    internal bool awaitingReview;
    internal IReadOnlyList<AuBtechReviewAgent.ScreeningLog>? reviewLogs;
    internal CancellationTokenSource? monitorCts;
    internal bool subscribed;

    internal void AttachToRun(Guid id)
    {
        if (id != currentSessionId) editingNew = false;
        currentSessionId = id;
        var state = ReviewEngine.LoadState(id);
        if (state == null)
        {
            stage = "";
            failureMessage = "This run could not be found. It may have expired and been deleted.";
            isSearching = false;
            return;
        }

        ApplyState(state, File.Exists(StatePath(id)) ? SafeRead(StatePath(id)) : null);
        isSearching = ReviewEngine.IsRunActive(id);
        if (isSearching)
        {
            Subscribe();
            StartLogMonitoringLoop();
        }
    }

    internal string StatePath(Guid id) =>
        Path.Join(Runs.FolderOf(id), "transparent-process.json");

    internal static string? SafeRead(string path)
    {
        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fileStream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    internal void ApplyState(AuBtechReviewAgent.ReviewState state, string? rawJson)
    {
        // The saved ledger can be a little behind the live events; never let the progress bar move back.
        var s = AuBtechReviewAgent.RunProgress.KeepFurthest(State.Stats, state.Stats);
        // While the review is written and checked the ledger is not saved, so a page opened then would show the last
        // saved step (extraction) until the next event, which can be minutes away. Take the run's live step instead.
        if (ReviewEngine.LiveStats(currentSessionId) is { } live)
            lock (live) s = AuBtechReviewAgent.RunProgress.KeepFurthest(live, s);
        State.Stats = s;
        State.RemovedBeforeScreening = state.RemovedBeforeScreening;
        State.SearchStringYields = state.SearchStringYields;
        State.ExclusionGroups = AuBtechReviewAgent.ExclusionReasons.Count(state.Phases.Screening);
        runInfo = state;
        totalIdentified = s.TotalIdentified;
        screenedCount = s.Screened;
        excludedCount = s.Excluded + s.FailedPeerReviewCheck;
        includedCount = s.Included;
        State.PassedPeerReviewCount = s.PassedPeerReviewCheck;
        State.FailedPeerReviewCount = s.FailedPeerReviewCheck;
        stage = s.ProcessingStage;
        queuePosition = s.QueuePosition;
        failureMessage = state.FailureMessage;
        completedUtc = state.CompletedUtc;
        awaitingReview = stage == AuBtechReviewAgent.PrismaReviewEngine.StageAwaitingReview && ReviewEngine.IsAwaitingScreeningReview(currentSessionId);
        if (awaitingReview) reviewLogs ??= state.Phases.Screening.ToList();
        else reviewLogs = null;
        if (rawJson != null) rawJsonLogs = rawJson;
    }

    internal void Subscribe()
    {
        if (subscribed) return;
        ReviewEngine.OnProgressUpdated += HandleEngineProgress;
        subscribed = true;
    }

    internal void Unsubscribe()
    {
        if (!subscribed) return;
        ReviewEngine.OnProgressUpdated -= HandleEngineProgress;
        subscribed = false;
    }

    internal void OnScreeningReviewSubmitted()
    {
        awaitingReview = false;
        reviewLogs = null;
        stage = AuBtechReviewAgent.PrismaReviewEngine.StageSynthesizing;
        StateHasChanged();
    }

    internal void StartLogMonitoringLoop()
    {
        monitorCts?.Cancel();
        monitorCts = new CancellationTokenSource();
        var token = monitorCts.Token;
        Guid runId = currentSessionId;
        // Parses the ledger only when it changed since the last tick (issue #306).
        var ledger = new AuBtechReviewAgent.RunLedgerWatch(StatePath(runId));

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && isSearching && runId == currentSessionId)
            {
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { break; }
                if (ledger.Read() is not { } read) continue;
                var (state, raw) = read;
                try
                {
                    await InvokeAsync(() =>
                    {
                        if (runId != currentSessionId) return;
                        ApplyState(state, raw);
                        StateHasChanged();
                    });
                }
                catch { /* the page went away while the state was shown; the loop ends or tries again next tick */ }
            }
        });
    }

    internal void HandleEngineProgress(Guid runSessionId, ReviewStats updatedStats)
    {
        // The engine is shared by all users; only react to this dashboard's own run.
        if (runSessionId != currentSessionId) return;

        InvokeAsync(() =>
        {
            State.Stats = updatedStats;
            totalIdentified = updatedStats.TotalIdentified;
            screenedCount = updatedStats.Screened;
            excludedCount = updatedStats.Excluded;
            includedCount = updatedStats.Included;
            
            State.PassedPeerReviewCount = updatedStats.PassedPeerReviewCheck;
            State.FailedPeerReviewCount = updatedStats.FailedPeerReviewCheck;
            
            stage = updatedStats.ProcessingStage;
            queuePosition = updatedStats.QueuePosition;
            excludedCount = updatedStats.Excluded + updatedStats.FailedPeerReviewCheck;
            if (stage == AuBtechReviewAgent.PrismaReviewEngine.StageAwaitingReview && !awaitingReview)
            {
                var state = ReviewEngine.LoadState(currentSessionId);
                if (state != null) ApplyState(state, null);
            }
            if (AuBtechReviewAgent.PrismaReviewEngine.IsFinishedStage(stage))
            {
                if (isSearching) OnRunFinished();
                isSearching = false;
            }
            
            StateHasChanged();
        });
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Pages;

// Starting a review, what happens when it finishes, and the tab title and announcements while it runs.
public partial class Home
{
    /// <summary>The browser tab's title: the progress while running, "✓ Review ready" when it finished here.</summary>
    internal string TabTitle()
    {
        if (!Composing && isSearching) return $"{AuBtechReviewAgent.RunProgress.Percent(State.Stats)}% · Your review";
        if (!Composing && finishedWhileOpen && stage == AuBtechReviewAgent.PrismaReviewEngine.StageComplete) return "✓ Review ready";
        if (!Composing && finishedWhileOpen && AuBtechReviewAgent.PrismaReviewEngine.IsFinishedStage(stage)) return "Run stopped · Your review";
        return RunId != null && !Composing ? "Your review" : "Start a review";
    }

    internal void OnRunFinished()
    {
        finishedWhileOpen = true;
        bool ok = stage == AuBtechReviewAgent.PrismaReviewEngine.StageComplete;
        _ = NotifyAsync(ok ? "Your review is ready" : "Your review stopped",
            ok ? "The report and its checked citations are ready in TraceableAI." : "Open TraceableAI to see what happened.");
    }

    internal async Task NotifyAsync(string title, string body)
    {
        try { await JS.InvokeAsync<bool>("traceableReview.notifyDone", title, body); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not show the browser notification: {Message}", ex.Message);
        }
    }

    internal async Task AskForNotification()
    {
        try { notifyPermission = await JS.InvokeAsync<string>("traceableReview.requestNotify"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not ask for notification permission: {Message}", ex.Message);
        }
    }

    /// <summary>One short sentence for the live region: changes only when the step or the outcome changes.</summary>
    internal string Announcement()
    {
        if (Composing || !hasRun) return "";
        if (stage == AuBtechReviewAgent.PrismaReviewEngine.StageComplete) return "The review is complete. The report is ready.";
        if (stage == AuBtechReviewAgent.PrismaReviewEngine.StageFailed) return "The run failed. " + (failureMessage ?? "");
        if (stage == AuBtechReviewAgent.PrismaReviewEngine.StageInterrupted) return "The run was interrupted.";
        if (awaitingReview) return "The run is waiting for you to check the screening decisions.";
        if (stage == AuBtechReviewAgent.PrismaReviewEngine.StageQueued) return "The run is waiting for a free slot.";
        return AuBtechReviewAgent.RunProgress.Steps.TryGetValue(State.Stats.ProgressStep, out var step) ? $"Now: {step.Label}." : "The run has started.";
    }


    internal void ToggleLog() => showLog = !showLog;

    /// <summary>The title of a run: its objective, or its query when the run has none.</summary>
    internal string ObjectiveShown() =>
        !string.IsNullOrWhiteSpace(runInfo?.ReviewObjective) ? runInfo!.ReviewObjective
        : runInfo == null && !string.IsNullOrWhiteSpace(reviewObjective) ? reviewObjective
        : runInfo?.SearchQuery ?? searchQuery;

    internal List<string> SourcesShown() => runInfo?.SelectedSources ?? State.SelectedSources.ToList();

    internal async Task StartReviewProcess()
    {
        if (isSearching) return;

        // Check the text fields before anything is reserved or spent: too-long fields are errors, and
        // instruction-like phrases must be confirmed once (they are then recorded in the protocol).
        var draft = new AuBtechReviewAgent.ReviewRequest(searchQuery, reviewObjective, inclusionCriteria, exclusionCriteria, maxResults,
            SynthesisDirective: synthesisTargetDirective, ArtifactKind: State.ArtifactKind, SearchStrings: reviewedSearchStrings,
            Method: ReviewMethod.Key, ResearchQuestions: AuBtechReviewAgent.ReviewInputGuard.ResearchQuestionLines(researchQuestionsText));
        inputCheck = AuBtechReviewAgent.ReviewInputGuard.Check(draft);
        if (!inputCheck.IsValid)
        {
            FocusFirstInvalidField();
            return;
        }
        string flagSignature = string.Join("|", inputCheck.Flags.Select(f => f.ToString()));
        if (inputCheck.Flags.Count > 0 && confirmedFlagSignature != flagSignature)
        {
            pendingFlagSignature = flagSignature;
            return;
        }

        // Count the run before it starts. The check happens here on the server, so it cannot be skipped
        // from the browser, and a double click cannot start two runs on one remaining slot.
        var tier = currentTier;
        if (!Quota.TryReserve(clientAddress, tier, out var reservedStatus, out var lease))
        {
            quotaStatus = reservedStatus;
            quotaNotice = reservedStatus.Message;
            return;
        }
        quotaStatus = reservedStatus;
        quotaNotice = "";

        var runSources = SelectedAvailableSources();
        if (runSources.Count == 0)
        {
            if (lease != null) Quota.Refund(lease);
            RefreshQuotaAndSources();
            quotaNotice = "Select at least one source.";
            return;
        }
        int runMaxResults = Quota.MaxResultsFor(tier, maxResults);
        maxResults = runMaxResults;
        // The engine runs the same check again; these are the normalised fields.
        showLog = false;
        prefillNotice = null;
        string cleanQuery = inputCheck.Request.Query;
        string cleanObjective = inputCheck.Request.Objective;
        string cleanInclusion = inputCheck.Request.Inclusion;
        string cleanExclusion = inputCheck.Request.Exclusion;
        string cleanDirective = inputCheck.Request.SynthesisDirective;
        pendingFlagSignature = null;

        State.Stats = new ReviewStats();
        State.RemovedBeforeScreening = new();
        State.SearchStringYields = new();
        State.ExclusionGroups = new();
        totalIdentified = 0;
        screenedCount = 0;
        excludedCount = 0;
        includedCount = 0;
        State.PassedPeerReviewCount = 0;
        State.FailedPeerReviewCount = 0;
        failureMessage = null;
        completedUtc = null;
        awaitingReview = false;
        reviewLogs = null;
        stage = AuBtechReviewAgent.PrismaReviewEngine.StageQueued;
        isSearching = true;
        rawJsonLogs = "{\n  \"status\": \"Starting run...\"\n}";

        var runId = Guid.NewGuid();
        currentSessionId = runId;
        // Only this browser gets the run's edit key (kept in its local storage, never shown), so the run link
        // can be shared to read the report without letting anyone else delete or change the run.
        editKey = Runs.ClaimRun(runId);
        editKeyFor = runId;
        canEdit = true;
        try { await JS.InvokeAsync<bool>("traceableReview.saveEditKey", runId.ToString(), editKey); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not keep the edit key in this browser: {Message}", ex.Message);
        }
        await ClearDraftAsync();
        editingNew = false;
        runInfo = null;
        var request = new AuBtechReviewAgent.ReviewRequest(
            cleanQuery, cleanObjective, cleanInclusion, cleanExclusion, runMaxResults,
            requirePeerReview, cleanDirective, sessionKeys, runSources, yearFrom, yearTo, State.ReviewScreeningFirst,
            DualScreening: State.DualScreening, CitationChaining: State.CitationChaining, ArtifactKind: State.ArtifactKind,
            SearchStrings: inputCheck.Request.SearchStrings, Method: ReviewMethod.Key,
            ResearchQuestions: inputCheck.Request.ResearchQuestions);

        Subscribe();

        _ = Task.Run(async () =>
        {
            try
            {
                await ReviewEngine.RunReviewAsync(runId, request);
            }
            catch (Exception ex)
            {
                // The run failed or was cancelled: give the quota slot back (a cancelled one once a day). The
                // reason is stored in the run's ledger, and the engine has logged it with the run ID.
                if (lease != null) Quota.GiveBackAfter(lease, ex);
                Logger.LogDebug("Run {RunId} ended with {Error}", runId, ex.GetType().Name);
            }
            finally
            {
                if (runId == currentSessionId)
                {
                    await InvokeAsync(() =>
                    {
                        if (isSearching) OnRunFinished();
                        isSearching = false;
                        var final = ReviewEngine.LoadState(runId);
                        if (final != null) ApplyState(final, SafeRead(StatePath(runId)));
                        RefreshQuotaAndSources();
                        StateHasChanged();
                    });
                }
            }
        });

        // Put the run in the address bar so a reload, a bookmark or another device can find it again.
        Nav.NavigateTo($"/review/{runId}", replace: true);
        StartLogMonitoringLoop();

        // On a phone the run panel is below the form: bring it into view so the click visibly did something.
        try { await JS.InvokeVoidAsync("traceableReview.revealOnSmallScreens", "run-panel"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not scroll to the run panel: {Message}", ex.Message);
        }
    }
}

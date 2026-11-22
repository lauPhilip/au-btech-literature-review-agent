using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Pages;

// The draft kept in this browser between visits, and pre-filling the form from an earlier run. It never holds API keys.
public partial class Home
{
    /// <summary>The review form as kept in this browser between visits. Never holds API keys.</summary>
    internal sealed record Draft(
        string Query, string Objective, string Inclusion, string Exclusion, int YearFrom, int YearTo,
        string ArtifactKind, string Directive, List<string> Sources, bool Dual, bool Chaining, bool Check, bool PeerReviewed,
        int MaxResults, DateTime SavedUtc, List<string>? SearchStrings = null, string? ResearchQuestions = null);

    internal bool FormIsEmpty() =>
        string.IsNullOrWhiteSpace(searchQuery) && string.IsNullOrWhiteSpace(reviewObjective)
        && string.IsNullOrWhiteSpace(inclusionCriteria) && string.IsNullOrWhiteSpace(exclusionCriteria)
        && string.IsNullOrWhiteSpace(researchQuestionsText);

    internal Draft CurrentDraft() => new(searchQuery, reviewObjective, inclusionCriteria, exclusionCriteria,
        yearFrom, yearTo, State.ArtifactKind, synthesisTargetDirective, State.SelectedSources.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        State.DualScreening, State.CitationChaining, State.ReviewScreeningFirst, requirePeerReview, maxResults, DateTime.MinValue,
        reviewedSearchStrings?.ToList(), researchQuestionsText);

    // The draft without its timestamp, to tell whether anything changed since the last save.
    internal string DraftFingerprint() => JsonSerializer.Serialize(CurrentDraft());

    /// <summary>Saves the form to this browser after a change, so a closed tab or a crash does not lose it.</summary>
    internal async Task SaveDraftAsync()
    {
        if (!Composing || isSearching || !draftRestored) return;
        string fingerprint = DraftFingerprint();
        if (fingerprint == lastSavedDraft) return;
        lastSavedDraft = fingerprint;
        try
        {
            if (FormIsEmpty()) await JS.InvokeAsync<bool>("traceableReview.clearDraft");
            else await JS.InvokeAsync<bool>("traceableReview.saveDraft", JsonSerializer.Serialize(CurrentDraft() with { SavedUtc = DateTime.UtcNow }));
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not save the form draft: {Message}", ex.Message);
        }
    }

    /// <summary>On a fresh, empty form, brings back the draft this browser kept (at most 30 days old).</summary>
    internal async Task RestoreDraftAsync()
    {
        try
        {
            if (!Composing || hasRun || From != null || !FormIsEmpty()) return;
            string? json = await JS.InvokeAsync<string?>("traceableReview.loadDraft");
            if (string.IsNullOrWhiteSpace(json)) return;
            var draft = JsonSerializer.Deserialize<Draft>(json);
            if (draft == null || draft.SavedUtc < DateTime.UtcNow.AddDays(-30)) return;
            searchQuery = draft.Query; reviewObjective = draft.Objective;
            inclusionCriteria = draft.Inclusion; exclusionCriteria = draft.Exclusion;
            researchQuestionsText = draft.ResearchQuestions ?? "";
            if (draft.YearFrom > 0) yearFrom = draft.YearFrom;
            if (draft.YearTo > 0) yearTo = draft.YearTo;
            State.ArtifactKind = AuBtechReviewAgent.ArtifactKinds.Normalize(draft.ArtifactKind);
            synthesisTargetDirective = draft.Directive;
            if (draft.Sources.Count > 0) State.SelectedSources = new HashSet<string>(draft.Sources, StringComparer.OrdinalIgnoreCase);
            State.DualScreening = draft.Dual; State.CitationChaining = draft.Chaining; State.ReviewScreeningFirst = draft.Check;
            requirePeerReview = draft.PeerReviewed;
            if (draft.MaxResults > 0) maxResults = Math.Min(draft.MaxResults, maxResultsCeiling);
            reviewedSearchStrings = draft.SearchStrings?.Take(AuBtechReviewAgent.ReviewInputGuard.MaxSearchStrings).ToList();
            prefillNotice = $"Your unfinished review from {draft.SavedUtc:d MMM} was brought back. It is kept in this browser only.";
            showStartEmpty = true;
        }
        catch (Exception ex) when (ex is JSException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not restore the form draft: {Message}", ex.Message);
        }
        finally
        {
            draftRestored = true;
            lastSavedDraft = DraftFingerprint();
        }
    }

    internal async Task StartEmpty()
    {
        searchQuery = reviewObjective = inclusionCriteria = exclusionCriteria = researchQuestionsText = "";
        reviewedSearchStrings = null;
        synthesisTargetDirective = AuBtechReviewAgent.ArtifactKinds.DefaultInstruction(State.ArtifactKind);
        prefillNotice = null;
        showStartEmpty = false;
        await ClearDraftAsync();
    }

    internal async Task ClearDraftAsync()
    {
        lastSavedDraft = "";
        try { await JS.InvokeAsync<bool>("traceableReview.clearDraft"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not clear the form draft: {Message}", ex.Message);
        }
    }

    /// <summary>Opens the form for a new review, prefilled with the current settings; the shown run stays at its own link.</summary>
    internal void StartNewDraft()
    {
        editingNew = true;
        showLog = false;
        confirmDelete = false;
    }

    /// <summary>Fills the form with an earlier run's settings (the reviewer still presses Start review).</summary>
    internal void PrefillFrom(Guid id)
    {
        var old = ReviewEngine.LoadState(id);
        if (old == null)
        {
            prefillNotice = "That run could not be found, so the form was not changed. Runs are deleted after a few days.";
            return;
        }
        currentSessionId = Guid.Empty; // the form starts a new run; the old one stays available at its own link
        stage = "";
        runInfo = null;
        editingNew = true;
        searchQuery = old.SearchQuery;
        if (!string.IsNullOrWhiteSpace(old.ReviewObjective)) reviewObjective = old.ReviewObjective;
        researchQuestionsText = string.Join("\n", old.ResearchQuestions ?? new List<string>());
        if (!string.IsNullOrWhiteSpace(old.InclusionCriteria)) inclusionCriteria = old.InclusionCriteria;
        if (!string.IsNullOrWhiteSpace(old.ExclusionCriteria)) exclusionCriteria = old.ExclusionCriteria;
        if (!string.IsNullOrWhiteSpace(old.ArtifactKind)) State.ArtifactKind = AuBtechReviewAgent.ArtifactKinds.Normalize(old.ArtifactKind);
        synthesisTargetDirective = old.SynthesisTargetDirective ?? "";
        if (old.YearFrom > 0) yearFrom = old.YearFrom;
        if (old.YearTo > 0) yearTo = old.YearTo;
        requirePeerReview = old.PeerReviewOnlyToggle;
        State.DualScreening = old.DualScreeningRequested;
        State.CitationChaining = old.CitationChainingRequested;
        State.ReviewScreeningFirst = old.HumanScreeningReviewRequested;
        if (old.SelectedSources.Count > 0) State.SelectedSources = new HashSet<string>(old.SelectedSources, StringComparer.OrdinalIgnoreCase);
        if (old.MaxResultsPerSource > 0) maxResults = old.MaxResultsPerSource;
        prefillNotice = string.IsNullOrWhiteSpace(old.ReviewObjective)
            ? "The query and options were copied from the earlier run. Runs made before this feature did not store the objective and criteria; copy them from that run's protocol."
            : "The settings of the earlier run were copied. Check them, change what you like, and press Start review.";
    }
}

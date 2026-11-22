using Microsoft.AspNetCore.Components;

namespace AuBtechReviewAgent.Components.Pages;

// The review form: previewing search strings, the year range, the blocker that keeps Start from running, and moving focus to a field with a problem.
public partial class Home
{
    internal async Task PreviewSearchStrings()
    {
        if (previewingStrings || previewsLeft <= 0 || string.IsNullOrWhiteSpace(searchQuery)) return;
        previewingStrings = true;
        previewError = null;
        previewsLeft--;
        try
        {
            var strings = await ReviewEngine.PreviewSearchStringsAsync(searchQuery, reviewObjective, inclusionCriteria, sessionKeys);
            reviewedSearchStrings = strings.Take(AuBtechReviewAgent.ReviewInputGuard.MaxSearchStrings).ToList();
            previewQuery = searchQuery.Trim();
            if (reviewedSearchStrings.Count == 0)
                previewError = "The model did not propose other phrasings. Add your own, or let the model try again during the run.";
        }
        catch (Exception ex)
        {
            Logger.LogInformation("Search-string preview failed: {Error}", ex.GetType().Name);
            previewError = "The preview did not work just now. The model will propose the search strings during the run instead.";
        }
        finally
        {
            previewingStrings = false;
        }
    }

    internal async Task ConfirmFlaggedInput()
    {
        confirmedFlagSignature = pendingFlagSignature;
        await StartReviewProcess();
    }
    internal void RefreshQuotaAndSources()
    {
        quotaStatus = Quota.GetStatus(clientAddress, currentTier);
        sourceAvailability = ReviewEngine.GetSourceAvailability(sessionKeys);
        if (maxResults > maxResultsCeiling) maxResults = maxResultsCeiling;
    }

    internal List<string> SelectedAvailableSources() =>
        State.SelectedSources.Where(k => sourceAvailability.TryGetValue(k, out var ok) && ok).ToList();

    internal void SetYears(int from)
    {
        yearFrom = from;
        yearTo = DateTime.UtcNow.Year;
    }

    /// <summary>Why the review cannot start yet, or null when it can. Shown next to the button, not after a click.</summary>
    internal string? StartBlocker()
    {
        if (!quotaStatus.Allowed) return string.IsNullOrWhiteSpace(quotaStatus.Message) ? "No runs left for now." : quotaStatus.Message;
        if (string.IsNullOrWhiteSpace(searchQuery)) return "Add a search query.";
        if (string.IsNullOrWhiteSpace(inclusionCriteria)) return "Say which studies to include, under Criteria.";
        if (SelectedAvailableSources().Count == 0) return "Select at least one database, under Criteria.";
        return null;
    }

    /// <summary>The field to go to for the current blocker, or null when no field can fix it (quota).</summary>
    internal string? BlockerTarget()
    {
        if (!quotaStatus.Allowed) return null;
        if (string.IsNullOrWhiteSpace(searchQuery)) return "f-query";
        if (string.IsNullOrWhiteSpace(inclusionCriteria)) return "f-inclusion";
        if (SelectedAvailableSources().Count == 0) return "f-sources";
        return null;
    }

    internal void FocusField(string fieldId)
    {
        if (CriteriaFields.Contains(fieldId)) criteriaOpen = true;
        pendingFocus = fieldId;
    }

    internal void GoToProblem()
    {
        if (BlockerTarget() is { } fieldId) FocusField(fieldId);
    }

    /// <summary>Called by the composer and the criteria panel on every change, so the page re-checks what is missing and saves the draft.</summary>
    internal void FormChanged() { }

    /// <summary>"Start review" is never silently disabled: when something is missing it takes you there instead.</summary>
    internal async Task StartOrShowProblem()
    {
        if (StartBlocker() != null)
        {
            GoToProblem();
            return;
        }
        await StartReviewProcess();
    }

    // The input guard names fields by these labels; this is where each one lives in the form.
    internal static readonly (string Name, string FieldId)[] FieldsByName =
    {
        ("Search query", "f-query"), ("Review objective", "f-objective"), ("Research question", "f-questions"),
        ("Inclusion criteria", "f-inclusion"), ("Exclusion criteria", "f-exclusion"), ("Artifact target", "f-directive"),
    };

    internal void FocusFirstInvalidField()
    {
        if (inputCheck is not { IsValid: false }) return;
        foreach (var error in inputCheck.Errors)
        {
            var field = FieldsByName.FirstOrDefault(f => error.StartsWith(f.Name, StringComparison.Ordinal));
            if (field.FieldId != null)
            {
                FocusField(field.FieldId);
                return;
            }
        }
    }

    internal string AdvancedSummary()
    {
        var on = new List<string>();
        if (State.DualScreening) on.Add("dual screening");
        if (State.CitationChaining) on.Add("chaining");
        if (State.ReviewScreeningFirst) on.Add("your check");
        if (requirePeerReview) on.Add("peer-reviewed");
        return on.Count == 0 ? $"{maxResults} per source" : $"{string.Join(", ", on)} · {maxResults} per source";
    }

    /// <summary>Fills the form with a worked example, so a first-time visitor sees what good input looks like.</summary>
    internal void LoadExample()
    {
        searchQuery = "agentic AI software frameworks";
        reviewObjective = "Which safety and fault-tolerance mechanisms do agentic AI frameworks use, and how are they evaluated?";
        researchQuestionsText = "Which safety and fault-tolerance mechanisms do agentic AI frameworks use?\nHow are these mechanisms evaluated, and with what evidence?";
        inclusionCriteria = "Studies that describe or evaluate an agent framework or orchestration loop, with some form of evaluation.";
        exclusionCriteria = "Studies about agriculture or marketing; opinion pieces without evidence.";
        SetYears(2020);
        prefillNotice = "An example review was filled in. Change anything you like, or start it as it is.";
    }

    // The exclusion criterion is optional, so an empty one reads "None". A run from before the criteria were stored
    // (no inclusion criterion either) points to the protocol, as the Include row does (issue #152).
    internal string ExclusionShown() =>
        runInfo == null ? (string.IsNullOrWhiteSpace(exclusionCriteria) ? "None" : exclusionCriteria)
        : !string.IsNullOrWhiteSpace(runInfo.ExclusionCriteria) ? runInfo.ExclusionCriteria
        : string.IsNullOrWhiteSpace(runInfo.InclusionCriteria) ? "(see the protocol)" : "None";
}

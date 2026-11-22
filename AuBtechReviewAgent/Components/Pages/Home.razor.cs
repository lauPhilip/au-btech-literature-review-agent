using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Pages;

// The New review page's state: the form fields kept in UiStateContainer, the run being followed and the dialog fields. The markup is in Home.razor, HomeRunView.razor and HomeKeyDialog.razor.
public partial class Home
{
    [Inject] internal UiStateContainer State { get; set; } = default!;

    internal string searchQuery { get => State.SearchQuery; set => State.SearchQuery = value; }
    internal string reviewObjective { get => State.ReviewObjective; set => State.ReviewObjective = value; }
    // The research questions as typed, one per line; ReviewInputGuard.ResearchQuestionLines splits them.
    internal string researchQuestionsText { get => State.ResearchQuestions; set => State.ResearchQuestions = value; }
    internal string inclusionCriteria { get => State.InclusionCriteria; set => State.InclusionCriteria = value; }
    internal string exclusionCriteria { get => State.ExclusionCriteria; set => State.ExclusionCriteria = value; }
    internal bool requirePeerReview { get => State.RequirePeerReview; set => State.RequirePeerReview = value; }
    internal string synthesisTargetDirective { get => State.SynthesisTargetDirective; set => State.SynthesisTargetDirective = value; }

    internal string rawJsonLogs { get => State.RawJsonLogs; set => State.RawJsonLogs = value; }
    internal int totalIdentified { get => State.TotalIdentified; set => State.TotalIdentified = value; }
    internal int screenedCount { get => State.ScreenedCount; set => State.ScreenedCount = value; }
    internal int excludedCount { get => State.ExcludedCount; set => State.ExcludedCount = value; }
    internal int includedCount { get => State.IncludedCount; set => State.IncludedCount = value; }
    internal bool isSearching { get => State.IsSearching; set => State.IsSearching = value; }

    internal int maxResults { get; set; } = 5;

    internal bool showKeyModal = false;
    internal AuBtechReviewAgent.UserApiKeys? sessionKeys = null;

    // Search-string preview: null until the reviewer previews (the model then chooses during the run).
    internal List<string>? reviewedSearchStrings;
    internal string? previewQuery;
    internal string? previewError;
    internal bool previewingStrings;
    internal int previewsLeft = 5; // each preview is one model call; a page gets a few

    // Run quota: the client's address is read once when the component starts (from the HTTP request
    // behind the prerender or the SignalR connection) and used as the quota key for this session.
    internal string clientAddress = "unknown";

    // Captured during prerendering (a normal HTTP request, so the address is reliable) and carried into
    // the interactive circuit through persisted component state, which Blazor Server protects with Data
    // Protection so the browser cannot change it. The HttpContextAccessor value is only a fallback.
    [PersistentState]
    public string? PrerenderClientAddress { get; set; }
    internal string adminToken = "";
    internal string modalAdminToken = "";

    // Result of ReviewInputGuard for the last "Start review" click, and the flags the user confirmed.
    internal AuBtechReviewAgent.InputCheck? inputCheck;
    internal string? pendingFlagSignature;
    internal string? confirmedFlagSignature;

    internal string quotaNotice = "";
    internal AuBtechReviewAgent.QuotaStatus quotaStatus = new(AuBtechReviewAgent.QuotaTier.Free, true, 0, 0, "");
    internal IReadOnlyDictionary<string, bool> sourceAvailability = new Dictionary<string, bool>();

    internal bool hasOwnMistralKey => !string.IsNullOrWhiteSpace(sessionKeys?.MistralApiKey);
    internal AuBtechReviewAgent.QuotaTier currentTier => Quota.ResolveTier(hasOwnMistralKey, adminToken);
    internal int maxResultsCeiling => Quota.MaxResultsFor(currentTier, 50);

    internal string modalMistral = "";
    internal string modalElsevier = "";
    internal string modalIeee = "";
    internal string modalScholar = "";

    [Inject] internal AuBtechReviewAgent.RunsOptions RunsOptions { get; set; } = default!;

    /// <summary>Set when the page is opened as /review/{RunId}: shows that run (and resumes following it if it is still going).</summary>
    [Parameter] public Guid? RunId { get; set; }

    /// <summary>/review?from={runId}: start a new review with the settings of an earlier run.</summary>
    [SupplyParameterFromQuery(Name = "from")] public Guid? From { get; set; }

    /// <summary>The kind of review chosen on the start page (/start); unknown or not-yet-available kinds fall back to the systematic review.</summary>
    [SupplyParameterFromQuery(Name = "method")] public string? MethodKey { get; set; }
    internal AuBtechReviewAgent.ReviewMethod ReviewMethod => AuBtechReviewAgent.ReviewMethods.AvailableOrDefault(MethodKey);
    internal Guid? prefilledFrom;
    internal string? prefillNotice;

    // The form is shown while composing (no run on this page, or "New review" pressed); otherwise a compact
    // summary of what was asked, so the run itself gets the attention.
    internal bool editingNew;
    internal bool Composing => !hasRun || editingNew;
    // The criteria panel: null until the reviewer folds or opens it (then open from 1024 px only).
    internal bool? criteriaOpen;
    internal AuBtechReviewAgent.ReviewState? runInfo;

    internal const string LabelClass = "ui-label";
    internal const string InputClass = "ui-input";

    internal int yearFrom
    {
        get => State.DateFrom.Year;
        set => State.DateFrom = new DateTime(Math.Clamp(value, 1900, DateTime.UtcNow.Year), 1, 1);
    }
    internal int yearTo
    {
        get => State.DateTo.Year;
        set => State.DateTo = new DateTime(Math.Clamp(value, 1900, DateTime.UtcNow.Year), 12, 31);
    }

    // Field to focus after the next render (a field in the criteria panel needs the panel open first).
    internal string? pendingFocus;

    /// <summary>The fields that live in the criteria panel rather than in the composer.</summary>
    internal static readonly HashSet<string> CriteriaFields = new() { "f-inclusion", "f-exclusion", "f-sources", "f-directive" };

    // ── Tab title, notifications and the remembered draft (speed and feel) ─────────────────────────────

    // Set when a run finished while this page was open, so the tab title says so until the next run.
    internal bool finishedWhileOpen;
    internal string notifyPermission = "unsupported";

    internal string lastSavedDraft = "";
    internal bool draftRestored;

    // Shown next to the "brought back" notice: start with an empty form instead.
    internal bool showStartEmpty;

    // ── Running and Done (redesign step 7) ─────────────────────────────────────────────────────────────
    internal bool showLog;

    internal Guid currentSessionId
    {
        get => State.CurrentSessionId;
        set => State.CurrentSessionId = value;
    }
    internal bool hasRun => State.CurrentSessionId != Guid.Empty;

    internal string stage = "";
    internal int queuePosition;
    internal string? failureMessage;
    internal DateTime? completedUtc;
    internal static string AppVersionShort => AuBtechReviewAgent.RecordingChatCompletionService.AppVersion.Split('+')[0];
    protected override void OnInitialized()
    {
        PrerenderClientAddress ??= AuBtechReviewAgent.RunQuotaService.NormalizeClientAddress(HttpContextAccessor.HttpContext?.Connection.RemoteIpAddress);
        clientAddress = PrerenderClientAddress;
        RefreshQuotaAndSources();
    }

    protected override void OnParametersSet()
    {
        if (From is Guid from && from != Guid.Empty && from != prefilledFrom && !isSearching)
        {
            prefilledFrom = from;
            PrefillFrom(from);
            return;
        }
        // Opening /review/{id} (after a reload, from a bookmark, or from another device) attaches this page to
        // that run. Opening plain /review keeps whatever run this tab already shows.
        if (RunId is Guid id && id != Guid.Empty && (id != currentSessionId || !subscribed))
        {
            // A run made by another kind of review opens on that module's own pages.
            if (AuBtechReviewAgent.ReviewModules.ElsewhereFor(Runs.LoadHeader(id), AuBtechReviewAgent.ReviewMethod.SystematicKey) is string elsewhere)
            {
                Nav.NavigateTo(elsewhere, replace: true);
                return;
            }
            AttachToRun(id);
        }
        else if (hasRun && !subscribed)
        {
            AttachToRun(currentSessionId);
        }
    }

    public void Dispose()
    {
        // The engine is a singleton: an event handler left behind would keep this page alive forever.
        Unsubscribe();
        monitorCts?.Cancel();
        monitorCts?.Dispose();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await SaveDraftAsync();
        await RefreshEditAccessAsync();
        if (pendingFocus is { } fieldId)
        {
            pendingFocus = null;
            try { await JS.InvokeVoidAsync("traceableReview.focus", fieldId); }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
            {
                Logger.LogDebug("Could not move focus to {Field}: {Message}", fieldId, ex.Message);
            }
        }
        if (keyModalJustOpened)
        {
            keyModalJustOpened = false;
            try { await JS.InvokeVoidAsync("traceableReview.trapFocus", "key-dialog"); }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
            {
                Logger.LogDebug("Could not move focus into the key dialog: {Message}", ex.Message);
            }
        }
        if (firstRender)
        {
            try
            {
                var tokenResult = await SessionStorage.GetAsync<string>("developer_access_token");
                if (tokenResult.Success && !string.IsNullOrWhiteSpace(tokenResult.Value))
                {
                    adminToken = modalAdminToken = tokenResult.Value;
                }

                var result = await SessionStorage.GetAsync<string>("encrypted_byok_payload");
                if (result.Success && !string.IsNullOrWhiteSpace(result.Value))
                {
                    sessionKeys = JsonSerializer.Deserialize<AuBtechReviewAgent.UserApiKeys>(result.Value);
                    
                    if (sessionKeys != null)
                    {
                        modalMistral = sessionKeys.MistralApiKey ?? "";
                        modalElsevier = sessionKeys.ElsevierApiKey ?? "";
                        modalIeee = sessionKeys.IeeeApiKey ?? "";
                        modalScholar = sessionKeys.ScholarApiKey ?? "";
                    }
                }
                RefreshQuotaAndSources();
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                // Browser storage can be unavailable (private window, blocked storage); the page works without saved keys.
                Logger.LogDebug("Could not restore saved keys from browser storage: {Message}", ex.Message.Replace("\r", " ").Replace("\n", " "));
            }

            // Independent of the keys above: the form draft and whether notifications are possible.
            await RestoreDraftAsync();
            try { notifyPermission = await JS.InvokeAsync<string>("traceableReview.notifyPermission"); }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
            {
                Logger.LogDebug("Could not read the notification permission: {Message}", ex.Message);
            }
            StateHasChanged();
        }
    }
}

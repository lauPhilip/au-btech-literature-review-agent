using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Pages;

// The API keys dialog, edit access to a run, copying its link and deleting it.
public partial class Home
{
    internal bool confirmDelete;
    // Set when the dialog opens, so the next render moves focus into it (OnAfterRenderAsync).
    internal bool keyModalJustOpened;

    internal void OpenKeyModal()
    {
        showKeyModal = true;
        keyModalJustOpened = true;
    }

    internal async Task CloseKeyModal()
    {
        showKeyModal = false;
        await ReturnFocusFromDialog();
    }

    internal async Task KeyDialogKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") await CloseKeyModal();
    }

    internal async Task ReturnFocusFromDialog()
    {
        try { await JS.InvokeVoidAsync("traceableReview.releaseFocus"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not return focus after the key dialog: {Message}", ex.Message);
        }
    }

    internal async Task SaveSessionKeys()
    {
        string sMistral = AuBtechReviewAgent.SecurityUtility.SanitizeApiKey(modalMistral);
        string sElsevier = AuBtechReviewAgent.SecurityUtility.SanitizeApiKey(modalElsevier);
        string sIeee = AuBtechReviewAgent.SecurityUtility.SanitizeApiKey(modalIeee);
        string sScholar = AuBtechReviewAgent.SecurityUtility.SanitizeApiKey(modalScholar);

        sessionKeys = new AuBtechReviewAgent.UserApiKeys(sMistral, sElsevier, sIeee, sScholar);

        string jsonPayload = JsonSerializer.Serialize(sessionKeys);
        await SessionStorage.SetAsync("encrypted_byok_payload", jsonPayload);

        adminToken = modalAdminToken.Trim();
        if (string.IsNullOrEmpty(adminToken)) await SessionStorage.DeleteAsync("developer_access_token");
        else await SessionStorage.SetAsync("developer_access_token", adminToken);
        quotaNotice = !string.IsNullOrEmpty(adminToken) && !Quota.IsValidAdminToken(adminToken)
            ? "The developer access token was not recognised." : "";

        RefreshQuotaAndSources();
        showKeyModal = false;
        await ReturnFocusFromDialog();
        StateHasChanged();
    }

    internal async Task ClearVault()
    {
        modalMistral = modalElsevier = modalIeee = modalScholar = modalAdminToken = adminToken = "";
        sessionKeys = null;
        await SessionStorage.DeleteAsync("encrypted_byok_payload");
        await SessionStorage.DeleteAsync("developer_access_token");
        quotaNotice = "";
        RefreshQuotaAndSources();
        showKeyModal = false;
        await ReturnFocusFromDialog();
        StateHasChanged();
    }

    // The run's edit key, read from this browser's storage; only the browser that started a run has it.
    internal string? editKey;
    internal Guid editKeyFor;
    internal bool canEdit;

    /// <summary>Looks up this browser's edit key for the run on the page, once per run.</summary>
    internal async Task RefreshEditAccessAsync()
    {
        if (!hasRun || currentSessionId == Guid.Empty || editKeyFor == currentSessionId) return;
        editKeyFor = currentSessionId;
        try { editKey = await JS.InvokeAsync<string?>("traceableReview.getEditKey", currentSessionId.ToString()); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not read the edit key: {Message}", ex.Message);
            editKey = null;
        }
        bool allowed = Runs.CanEdit(currentSessionId, editKey);
        if (allowed != canEdit)
        {
            canEdit = allowed;
            StateHasChanged();
        }
    }

    internal bool linkCopied;

    internal async Task CopyRunLink()
    {
        try { linkCopied = await JS.InvokeAsync<bool>("traceableReview.copyLink"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException) { linkCopied = false; }
    }

    /// <summary>Deletes the finished run shown on this page (its folder, report and downloaded papers).</summary>
    internal void DeleteRun()
    {
        confirmDelete = false;
        var id = currentSessionId;
        if (!Runs.DeleteRun(id, editKey))
        {
            failureMessage = "The run could not be deleted (it may still be running or already be gone).";
            return;
        }
        Unsubscribe();
        currentSessionId = Guid.Empty;
        State.Stats = new ReviewStats();
        State.RemovedBeforeScreening = new();
        State.SearchStringYields = new();
        State.ExclusionGroups = new();
        rawJsonLogs = "{\n  \"status\": \"Run deleted.\"\n}";
        stage = "";
        failureMessage = null;
        runInfo = null;
        Nav.NavigateTo("/review", replace: true);
    }
}

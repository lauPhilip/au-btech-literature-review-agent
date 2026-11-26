using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Layout;

/// <summary>
/// Keeps the browser tab's title in step with a page that changes it while it is open. The head is rendered once
/// (App.razor has a static HeadOutlet, so a page's PageTitle is fixed when it is first sent), so an interactive page
/// tells the browser each new title itself (window.traceableReview.setTitle) and only when it has changed.
/// </summary>
public sealed class TabTitleSync(IJSRuntime js, ILogger logger)
{
    private string? last;

    /// <summary>The title as SeoHead writes it: the page's title and the site's name.</summary>
    public static string Full(string title) =>
        title.StartsWith(SiteSeo.SiteName, StringComparison.Ordinal) ? title : $"{title} · {SiteSeo.SiteName}";

    /// <summary>Sends the title to the browser if it differs from the last one sent (the first call always sends: the
    /// interactive page may already be further on than what was rendered first).</summary>
    public async Task UpdateAsync(string title)
    {
        string full = Full(title);
        if (full == last) return;
        last = full;
        try { await js.InvokeVoidAsync("traceableReview.setTitle", full); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogDebug("Could not set the tab title: {Message}", ex.Message);
        }
    }
}

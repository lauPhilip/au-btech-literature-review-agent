using Microsoft.AspNetCore.Components;

namespace AuBtechReviewAgent.Components.Pages;

public partial class Metrics
{
    private static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.InvariantCulture;
    private static readonly (string Key, string Label)[] Kinds = { ("systematic", "Systematic reviews"), ("grey", "Grey literature reviews") };
    private static readonly (string Id, string Label)[] Sections = { ("chart", "Chart"), ("shares", "Shares per run"), ("run-data", "Run data"), ("effort", "Effort"), ("by-version", "By version") };

    // Table cells: square, with a header row that stays at the top of its box while the rows scroll.
    internal const string Td = "whitespace-nowrap border-t border-line px-3 py-2 text-ink";
    internal const string TdNum = "whitespace-nowrap border-t border-line px-3 py-2 text-right tabular-nums text-ink";
    internal const string ThClass = "sticky top-0 z-10 whitespace-nowrap border-b border-line bg-soft px-3 py-2 font-semibold text-muted";

    [SupplyParameterFromQuery(Name = "kind")] public string? KindParameter { get; set; }

    /// <summary>Show the made-up runs of <see cref="AuBtechReviewAgent.SampleMetrics"/> instead of the store (the development page /design/metrics).</summary>
    [Parameter] public bool Sample { get; set; }

    /// <summary>The grey literature tab is there while that review is described on the site (its card is on, or it is in preview).</summary>
    private bool ShowGrey => ModuleOptions.Shows("grey");
    private string Kind => ShowGrey && KindParameter == "grey" ? "grey" : "systematic";
    private bool Grey => Kind == "grey";
    private string PageHref => Grey ? "/metrics?kind=grey" : "/metrics";

    private bool loaded, allowed;
    private AuBtechReviewAgent.MetricsOverview overview = AuBtechReviewAgent.MetricsOverview.From(Array.Empty<AuBtechReviewAgent.RunMetrics>());
    private IEnumerable<AuBtechReviewAgent.RunMetrics> Newest => overview.Runs.Reverse();

    // A tab link changes only the query, so the other kind's runs are loaded here.
    protected override void OnParametersSet()
    {
        if (!loaded) return;
        Load();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        string? token = null;
        if (!Sample)
        {
            try
            {
                var stored = await SessionStorage.GetAsync<string>("developer_access_token");
                if (stored.Success) token = stored.Value;
            }
            catch (Exception ex)
            {
                // Browser storage can be unavailable (private window, blocked storage); the token can be typed in instead.
                Logger.LogDebug("Could not read the developer token from browser storage: {Message}", ex.Message);
            }
        }
        // Everyone sees the figures; only maintainers (developer token, or Development) get the report links.
        allowed = !Sample && Quota.ResolveTier(false, token) == AuBtechReviewAgent.QuotaTier.Developer;
        Load();
        loaded = true;
        StateHasChanged();
    }

    private void Load() =>
        overview = AuBtechReviewAgent.MetricsOverview.From(Sample
            ? AuBtechReviewAgent.SampleMetrics.Runs().Where(r => r.IsMultivocal == Grey)
            : Store.ReadAll(multivocal: Grey));

    private static string Pct(double share) => AuBtechReviewAgent.MetricsOverview.Percent(share);
}

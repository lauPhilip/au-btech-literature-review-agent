using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Modules.Multivocal.Pages;

// The page's state and the run it shows. The planning form's logic is in MultivocalStart.Planning.cs and the actions
// on a found source in MultivocalStart.Sources.cs; the parts of the page (MultivocalStartPart) read this state.
public partial class MultivocalStart
{
    [Parameter] public Guid? RunId { get; set; }

    // "grey" when started from the Grey Literature Review card.
    [SupplyParameterFromQuery(Name = "card")] public string? CardKey { get; set; }

    internal static readonly ReviewMethod Method = ReviewMethods.Find("multivocal")!;

    internal MultivocalPlan plan = new();
    internal PlannedRun? planned;
    internal string protocolText = "";
    internal MultivocalLedger? ledger;
    internal string stage = "";
    internal bool canEdit;
    internal string? editKey;
    internal MultivocalScreeningFile? screening;
    internal Dictionary<string, GreyScreening> decisions = new();
    internal string? screenError;
    internal string sourceFilter = "all";
    internal MultivocalQualityFile? quality;
    internal Dictionary<string, GreyQuality> qualities = new();
    internal string? qualityError;
    internal readonly Dictionary<string, string> reviewNotes = new();
    internal MultivocalMapFile? map;
    internal MultivocalExtractionFile? extraction;
    internal MultivocalSynthesisFile? synthesis;
    internal Dictionary<string, GreyExtraction> extractions = new();
    internal string? reviewing;
    internal string? retrying;
    internal Dictionary<string, PageSnapshot> snapshots = new();
    internal readonly HashSet<string> keeping = new();
    internal string? keepError;
    internal int step;
    internal string panelTab = "plan";
    internal MultivocalPaperFile? paper;
    internal string notifyPermission = "unsupported";
    private bool wasRunning;
    private bool tabChosen; // once the reviewer picks a tab, the panel stops following the run
    private string? shownStep;
    internal bool saving;
    internal string? error;

    // Grey literature only while the formal pool is not built (decision 6), whichever card planned the review.
    internal bool GreyOnly => !(planned?.Plan.UsesFormalPool ?? plan.UsesFormalPool);
    internal MultivocalPlan Shown => planned?.Plan ?? plan;

    // Whether the card this review is planned from is switched on; when it is not, the page is a preview.
    internal bool CardOn => ReviewMethods.Find(Shown.Card)?.Available == true;

    protected override void OnParametersSet()
    {
        var module = ReviewModules.Find("multivocal")!;
        if (!ModuleOptions.CanOpen(module)) { Nav.NotFound(); return; }

        if (RunId is Guid id)
        {
            // A systematic run opened here goes to its own page.
            if (ReviewModules.ElsewhereFor(Runs.LoadHeader(id), module.Key) is string elsewhere)
            {
                Nav.NavigateTo(elsewhere, replace: true);
                return;
            }
            Reload(id);
            return;
        }
        // The multivocal card is planned only while it is on or its module is in preview; otherwise every review
        // planned here is a grey literature review, which is what its card promises.
        bool multivocalOpen = Method.Available || ModuleOptions.IsPreview(module.Key);
        plan.Card = CardKey == "grey" || !multivocalOpen ? "grey" : "multivocal";
        StartFormalPool();
    }

    protected override void OnInitialized() => Runner.Changed += OnRunChanged;

    public void Dispose() => Runner.Changed -= OnRunChanged;

    // Reads every file of the run again; the panel shows the progress, with the report once it is ready, unless the
    // reviewer chose a tab.
    private void Reload(Guid id)
    {
        planned = Planner.Load(id);
        protocolText = Runs.ReadProtocol(id) ?? "";
        ledger = Searcher.LoadLedger(id);
        snapshots = Pages.Load(id).Pages.ToDictionary(p => MultivocalSearcher.AddressKey(p.Url));
        string oldDefault = DefaultFilter();
        SetScreening(Screener.Load(id));
        SetQuality(Quality.Load(id));
        map = Mapper.Load(id);
        SetExtraction(Extractor.Load(id));
        synthesis = Synthesiser.Load(id);
        paper = Writer.Load(id);
        if (sourceFilter == oldDefault) sourceFilter = DefaultFilter();
        stage = Runs.LoadHeader(id)?.Stage ?? "";
        if (!tabChosen) panelTab = "progress";
    }

    private void SetScreening(MultivocalScreeningFile? file)
    {
        screening = file;
        decisions = new();
        if (file == null) return;
        foreach (var s in file.Sources) decisions.TryAdd(s.Address, s);
    }

    private void SetQuality(MultivocalQualityFile? file)
    {
        quality = file;
        qualities = new();
        if (file == null) return;
        foreach (var q in file.Sources) qualities.TryAdd(q.Address, q);
    }

    private void SetExtraction(MultivocalExtractionFile? file)
    {
        extraction = file;
        extractions = new();
        if (file == null) return;
        foreach (var x in file.Sources) extractions.TryAdd(x.Address, x);
    }

    private string DefaultFilter() => quality != null ? "passed" : screening != null ? "included" : "all";

    internal void ChooseTab(string tab)
    {
        panelTab = tab;
        tabChosen = true;
    }

    // The runner reports from its own thread: progress lines only redraw, a new step or the end reads the files again.
    private void OnRunChanged(Guid id)
    {
        if (planned?.RunId != id) return;
        _ = InvokeAsync(() =>
        {
            var status = Runner.StatusOf(id);
            if (status == null || status.Step != shownStep || !status.Running)
            {
                Reload(id);
                shownStep = status?.Step;
            }
            if (wasRunning && status is { Running: false })
            {
                bool done = stage == MultivocalWriter.StageChecked;
                _ = NotifyAsync(done ? "Your review is ready" : "Your review stopped",
                    done ? "The paper is written and its citations are checked in TraceableAI." : "Open TraceableAI to see what happened.");
            }
            wasRunning = status?.Running == true;
            StateHasChanged();
        });
    }

    private async Task NotifyAsync(string title, string body)
    {
        try { await JS.InvokeAsync<bool>("traceableReview.notifyDone", title, body); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not show the browser notification: {Message}", ex.Message);
        }
    }

    internal async Task AskForNotification()
    {
        try { notifyPermission = await JS.InvokeAsync<string>("traceableReview.requestNotify"); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not ask for notifications: {Message}", ex.Message);
        }
    }

    internal void ContinueRun()
    {
        if (planned == null) return;
        tabChosen = false;
        Runner.Start(planned.RunId, editKey);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The edit key lives only in the browser that planned the run.
        if (!firstRender || planned == null) return;
        try { editKey = await JS.InvokeAsync<string?>("traceableReview.getEditKey", planned.RunId.ToString()); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not read the edit key: {Message}", ex.Message);
        }
        canEdit = Runs.CanEdit(planned.RunId, editKey);
        wasRunning = Runner.IsRunning(planned.RunId);
        try { notifyPermission = await JS.InvokeAsync<string>("traceableReview.notifyPermission"); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not read the notification permission: {Message}", ex.Message);
        }
        StateHasChanged();
    }

    internal string StageLine()
    {
        if (stage == MultivocalWriter.StageChecked) return "Finished: the paper is written and every citation is checked";
        var status = planned == null ? null : Runner.StatusOf(planned.RunId);
        if (status is { Running: true }) return $"Running: {status.Step.ToLowerInvariant()}…";
        return $"Not finished: stopped before {MultivocalRunner.StepFor(stage).ToLowerInvariant()}";
    }
}

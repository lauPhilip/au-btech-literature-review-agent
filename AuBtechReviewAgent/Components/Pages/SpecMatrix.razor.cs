using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Components.Pages;

// The report page's state and how it is loaded from the run folder. The sections (SpecMatrix*.razor) read it through Page.
public partial class SpecMatrix
{
    [Inject] internal UiStateContainer State { get; set; } = default!;
    [Inject] internal ILogger<SpecMatrix> Logger { get; set; } = default!;

    /// <summary>Set when the page is opened as /spec-matrix/{RunId}, so a report can be reopened from a link.</summary>
    [Parameter] public Guid? RunId { get; set; }

    internal PrismaReport report = new PrismaReport();
    internal List<string> includedCitations = new();
    internal Dictionary<string, int> publicationsByYear = new();
    internal readonly List<IncludedPaperMetricRow> tableRecords = new();
    internal readonly List<AuBtechReviewAgent.StudyExtraction> extractions = new();
    internal readonly List<AuBtechReviewAgent.CitationSupportResult> auditChecks = new();
    /// <summary>How many citations the second repair pass removed (RemovedCitations in citation-audit.json).</summary>
    internal int auditRemoved;
    internal AuBtechReviewAgent.ThematicCodebook? codebook;
    internal int journalCount, conferenceCount, transactionsCount, proceedingsCount, preprintCount;

    internal sealed record PlatformShare(string Category, string Label, int Count, string Color, string Badge);
    internal List<PlatformShare> platformShares = new();

    // Literal class names so the Tailwind build finds them.
    internal static readonly string[] BarColors = { "bg-teal-500", "bg-orange-500", "bg-blue-400", "bg-yellow-400", "bg-purple-400", "bg-rose-400", "bg-lime-500", "bg-unverifiable-chart" };
    internal static readonly string[] BadgeColors = { "text-teal-800 bg-teal-50", "text-orange-800 bg-orange-50", "text-blue-800 bg-blue-50", "text-yellow-800 bg-yellow-50", "text-purple-800 bg-purple-50", "text-rose-800 bg-rose-50", "text-lime-800 bg-lime-50", "text-ink bg-soft" };

    internal bool peerReviewOnlyToggle;
    internal int passedPeerReviewCheck;
    internal int failedPeerReviewCheck;
    internal bool hasProtocol;
    internal string mermaidDiagramCode = "";

    // The citation marker the reader clicked: (field, sentence index, reference number).
    internal (string Field, int Sentence, int Reference)? openCitation;

    // The run's figures and its PRISMA flow, for the run line and Figure 2.1; null for a run without a ledger.
    internal AuBtechReviewAgent.ReviewStats? stats;
    internal AuBtechReviewAgent.PrismaFlowCounts? flow;
    internal string runLine = "";
    /// <summary>The printed front page (Components/Paper/ArticleFront.razor); null until the report is loaded.</summary>
    internal AuBtechReviewAgent.ArticleInfo? articleInfo;
    internal string dateLine = "";

    /// <summary>The sections of this report, for the contents list (the page gives each the same id).</summary>
    internal List<AuBtechReviewAgent.OutlineEntry> Outline => AuBtechReviewAgent.ReportOutline.Systematic(report);

    internal bool HasReport => !deleted && report != null && !string.IsNullOrEmpty(report.TitleItem) && report.TitleItem != "Pending...";

    internal static string ScrubAnchors(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return "";
        string clean = Regex.Replace(rawText, @"\[MERMAID_START\].*?\[MERMAID_END\]", "", RegexOptions.Singleline);
        clean = Regex.Replace(clean, @"\[TIKZ_START\].*?\[TIKZ_END\]", "", RegexOptions.Singleline);
        clean = Regex.Replace(clean, @"\[Source Context Anchor \d+.*?\]", "").Trim();
        return AuBtechReviewAgent.MethodsSectionWriter.StripMarkdownEmphasis(clean);
    }

    internal static string Deg(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One line under the title: studies and records, themes, citations checked (as the report counts them) and the app version.</summary>
    public static string RunLine(AuBtechReviewAgent.ReviewState state, AuBtechReviewAgent.VerdictTally citations)
    {
        var s = state.Stats;
        int cited = citations.Checked;
        var parts = new List<string> { $"{s.Included} {(s.Included == 1 ? "study" : "studies")} from {s.TotalIdentified} {(s.TotalIdentified == 1 ? "record" : "records")}" };
        if (s.ThemesIdentified > 0) parts.Add($"{s.ThemesIdentified} {(s.ThemesIdentified == 1 ? "theme" : "themes")}");
        if (cited > 0) parts.Add($"{cited} {(cited == 1 ? "citation" : "citations")} checked");
        string? version = state.RunSettings?.AppVersion;
        if (!string.IsNullOrWhiteSpace(version)) parts.Add("TraceableAI " + AuBtechReviewAgent.ToolVersion.Display(version));
        return string.Join(" · ", parts);
    }

    internal static bool InTier(IncludedPaperMetricRow r, string label) => label switch
    {
        "Conf" => r.VenueType is "Conferences" or "Proceedings",
        "Preprint" => r.VenueType is "Preprints" or "Other",
        _ => (r.Quartile ?? "").Equals(label, StringComparison.OrdinalIgnoreCase),
    };

    internal static string AppraisalCategory(AuBtechReviewAgent.StudyExtraction e) =>
        e.Error != null ? "not appraised" :
        e.AppraisalCategory == "not_empirical" ? "not empirical (MMAT does not apply)" :
        e.AppraisalCategory.Replace('_', ' ');

    /// <summary>A one-paragraph description of the included set, built only from the run's own data.</summary>
    // Section 3 points to the tables in the appendix (issue #139), with the same sentence as main.tex.
    internal string DataParagraph() => (AuBtechReviewAgent.MethodsSectionWriter.IncludedSet(tableRecords) + " " +
        AuBtechReviewAgent.MethodsSectionWriter.AppendixPointer(tableRecords.Count, extractions)).Trim();

    /// <summary>Reads the verdicts and re-checks from citation-audit.json (and, after a re-check, the report's check sentence).</summary>
    internal async Task LoadAuditAsync(string folder, bool reloadReport = true)
    {
        string auditPath = Path.Join(folder, "citation-audit.json");
        auditChecks.Clear();
        auditRemoved = 0;
        rechecks = new();
        if (File.Exists(auditPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
                if (doc.RootElement.TryGetProperty("SupportChecks", out var checks))
                    auditChecks.AddRange(AuBtechReviewAgent.ReportEdits.Checks(JsonSerializer.Deserialize<List<AuBtechReviewAgent.CitationSupportResult>>(checks.GetRawText()) ?? new(),
                        AuBtechReviewAgent.ReportEditLog.Load(folder)));
                if (doc.RootElement.TryGetProperty("RemovedCitations", out var removed) && removed.ValueKind == JsonValueKind.Array)
                    auditRemoved = removed.GetArrayLength();
                if (doc.RootElement.TryGetProperty("Rechecks", out var done))
                    rechecks = JsonSerializer.Deserialize<List<AuBtechReviewAgent.CitationRecheck>>(done.GetRawText()) ?? new();
            }
            catch (Exception ex) when (ex is JsonException or IOException) { auditChecks.Clear(); rechecks = new(); }
        }
        rechecksLeft = auditChecks.Count == 0 ? 0 : Math.Max(0, AuBtechReviewAgent.PrismaReviewEngine.MaxRechecksPerRun - rechecks.Count);

        string reportPath = Path.Join(folder, "prisma-report.json");
        if (reloadReport && File.Exists(reportPath))
        {
            try
            {
                var updated = JsonSerializer.Deserialize<PrismaReport>(await File.ReadAllTextAsync(reportPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (updated != null) report.CitationCheckSummary = AuBtechReviewAgent.ReportEdits.Apply(updated, AuBtechReviewAgent.ReportEditLog.Load(folder)).CitationCheckSummary;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Logger.LogDebug("Could not read the re-checked report sentence: {Message}", ex.Message);
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!InGallery && State.CurrentSessionId != Guid.Empty && editKeyFor != State.CurrentSessionId)
        {
            editKeyFor = State.CurrentSessionId;
            try { editKey = await JS.InvokeAsync<string?>("traceableReview.getEditKey", State.CurrentSessionId.ToString()); }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException) { editKey = null; }
            bool allowed = Runs.CanEdit(State.CurrentSessionId, editKey);
            if (allowed != canEdit) { canEdit = allowed; StateHasChanged(); }
        }
        if (pendingFocus is { } focusId)
        {
            pendingFocus = null;
            try { await JS.InvokeAsync<bool>("traceableReview.focus", focusId); }
            catch (JSException) { /* moving focus is a convenience; the page works without it */ }
            catch (JSDisconnectedException) { /* the tab was closed */ }
        }
        if (!string.IsNullOrEmpty(mermaidDiagramCode))
        {
            try
            {
                await JS.InvokeAsync<bool>("traceableMermaid.render", "mermaid-preview", mermaidDiagramCode);
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException)
            {
                Logger.LogDebug("Could not draw the diagram: {Message}", ex.Message);
            }
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        galleryEntry = null;
        if (InGallery)
        {
            galleryEntry = Gallery.Find(GalleryId);
            if (galleryEntry == null) { report = new PrismaReport(); return; }
        }
        else if (RunId is Guid id && id != Guid.Empty)
        {
            // A run made by another kind of review has its own report page.
            if (AuBtechReviewAgent.ReviewModules.ElsewhereFor(Runs.LoadHeader(id), AuBtechReviewAgent.ReviewMethod.SystematicKey) is string elsewhere)
            {
                Nav.NavigateTo(elsewhere, replace: true);
                return;
            }
            State.CurrentSessionId = id;
        }

        report = new PrismaReport();
        includedCitations = new();
        tableRecords.Clear();
        extractions.Clear();
        auditChecks.Clear();
        codebook = null;
        publicationsByYear = new();
        platformShares = new();
        journalCount = conferenceCount = transactionsCount = proceedingsCount = preprintCount = 0;
        peerReviewOnlyToggle = false;
        aiStatement = aiShort = "";
        researchQuestions = new();
        copyNotice = null;
        passedPeerReviewCheck = failedPeerReviewCheck = 0;
        mermaidDiagramCode = "";
        openCitation = null;
        deleted = false;
        stats = null;
        flow = null;
        runLine = dateLine = "";
        articleInfo = null;
        checkFilter = AuBtechReviewAgent.ReportOutline.Flagged;
        checkIndex = attentionIndex = -1;

        string folder = ReportFolder();
        string reportPath = Path.Join(folder, "prisma-report.json");
        // The protocol's link is a download of the run, which a gallery copy outlives.
        hasProtocol = !InGallery && File.Exists(Path.Join(folder, "protocol.md"));
        ResetEdits(folder);

        if (File.Exists(reportPath))
        {
            try
            {
                string content = await File.ReadAllTextAsync(reportPath);
                report = JsonSerializer.Deserialize<PrismaReport>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrismaReport();
                report = AuBtechReviewAgent.ReportEdits.Apply(report, editLog);
            }
            catch { report = new PrismaReport(); }
        }

        await LoadAuditAsync(folder, reloadReport: false);

        var state = LoadLedger();
        // The gallery shows a copy whose run may be gone, so its printout keeps the screen header instead of links to the run.
        if (HasReport && !InGallery)
            articleInfo = AuBtechReviewAgent.ArticleInfo.ForSystematic(ReportRunId, state, report, ScrubAnchors(report.TitleItem), ScrubAnchors(report.AbstractItem), hasProtocol);
        if (state == null) return;

        peerReviewOnlyToggle = state.PeerReviewOnlyToggle;
        stats = state.Stats;
        flow = AuBtechReviewAgent.PrismaFlowCounts.From(state);
        runLine = RunLine(state, AuBtechReviewAgent.VerdictTally.From(auditChecks, auditRemoved));
        dateLine = state.CompletedUtc is DateTime done ? done.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
        aiStatement = AuBtechReviewAgent.AiUseStatement.Methods(state, ReportRunId, editLog);
        aiShort = AuBtechReviewAgent.AiUseStatement.Short(state);
        researchQuestions = state.ResearchQuestions?.ToList() ?? new List<string>();
        passedPeerReviewCheck = state.Stats.PassedPeerReviewCheck;
        failedPeerReviewCheck = state.Stats.FailedPeerReviewCheck;
        if (state.Extractions != null) extractions.AddRange(state.Extractions);
        codebook = state.ThematicSynthesis;

        // Older runs have ReferenceNumber = 0 everywhere; OrderBy is stable, so they keep their order.
        tableRecords.AddRange(state.SynthesizedRecords.OrderBy(r => r.ReferenceNumber));
        fullTextUrls = state.FullTextUrls ?? new();
        notes = InGallery ? new() : Runs.LoadNotes(State.CurrentSessionId).ToList(); // the gallery never copies notes
        includedCitations = tableRecords.Select(r => r.ApaCitation.Trim().TrimEnd('.')).ToList();

        platformShares = tableRecords
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Other" : r.Category)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select((g, i) => new PlatformShare(g.Key, AuBtechReviewAgent.PrismaReviewEngine.CategoryLabel(g.Key), g.Count(),
                BarColors[i % BarColors.Length], BadgeColors[i % BadgeColors.Length]))
            .ToList();

        publicationsByYear = tableRecords
            .GroupBy(r => r.Year > 0 ? r.Year.ToString() : "n.d.")
            .OrderBy(g => g.Key == "n.d." ? "9999" : g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        journalCount = tableRecords.Count(r => r.VenueType == "Journals");
        transactionsCount = tableRecords.Count(r => r.VenueType == "Transactions");
        conferenceCount = tableRecords.Count(r => r.VenueType == "Conferences");
        proceedingsCount = tableRecords.Count(r => r.VenueType == "Proceedings");
        preprintCount = tableRecords.Count - journalCount - transactionsCount - conferenceCount - proceedingsCount;

        if (report.Artifact is { IsDiagram: true, Error: null } artifactDiagram)
        {
            mermaidDiagramCode = artifactDiagram.Mermaid;
        }
        else if (!string.IsNullOrEmpty(report.SynthesisResultsItem))
        {
            var mermaidMatch = Regex.Match(report.SynthesisResultsItem, @"\[MERMAID_START\](.*?)\[MERMAID_END\]", RegexOptions.Singleline);
            // Older runs stored the model's diagram unrepaired; clean it here as well.
            if (mermaidMatch.Success) mermaidDiagramCode = AuBtechReviewAgent.MermaidSanitizer.Sanitize(mermaidMatch.Groups[1].Value.Trim());
        }
    }
}

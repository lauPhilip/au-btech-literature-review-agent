using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    private readonly string _globalMistralKey;
    private readonly string _globalElsevierKey;
    private readonly string? _globalIeeeKey;
    private readonly string? _globalScholarKey;
    private readonly string _supportStatement;
    private readonly RunsOptions _runsOptions;

    /// <summary>Tracks live runs, the waiting line for run slots, and screening reviews in progress.</summary>
    public RunCoordinator Coordinator { get; }
    public RunsOptions RunsOptions => _runsOptions;

    // The engine is a singleton shared by every connected user, so progress events carry the session id
    // and each dashboard only reacts to its own run (previously every open dashboard showed the counts of
    // whichever run fired last).
    public event Action<Guid, ReviewStats>? OnProgressUpdated;

    public const string DefaultSupportStatement =
        "No funding statement has been declared for this review. The funding and support text can be set in the Report:SupportStatement configuration value.";

    /// <summary>The folder of one run (see <see cref="RunStore.FolderOf"/>).</summary>
    private string GetWorkspaceFolderPath(Guid sessionId) => Store.FolderOf(sessionId);

    /// <summary>The run folders: header, edit keys, notes and deleting, shared by every kind of review.</summary>
    public RunStore Store { get; }

    /// <summary>Folder holding one sub-folder per run. Defaults to ./WorkspaceStore.</summary>
    public string WorkspaceRoot { get => Store.WorkspaceRoot; init => Store.WorkspaceRoot = value; }

    /// <summary>Test hook: builds the chat service from an API key (default: Mistral via Semantic Kernel).</summary>
    public Func<string, IChatCompletionService>? ChatFactory { get; init; }

    /// <summary>Test hook: builds a source gateway from a SourceCatalog key (default: the real API adapters).</summary>
    public Func<string, IAcademicSource>? SourceFactory { get; init; }

    private string GetStateFilePath(Guid sessionId) => 
        Path.Join(GetWorkspaceFolderPath(sessionId), "transparent-process.json");

    private string GetReportFilePath(Guid sessionId) => 
        Path.Join(GetWorkspaceFolderPath(sessionId), "prisma-report.json");

    /// <summary>Contact e-mail and optional keys for OpenAlex, Semantic Scholar, Crossref and Unpaywall.</summary>
    public OpenSourcesOptions OpenSources { get; init; } = new();

    /// <summary>Which language model to use (Mistral or any OpenAI-compatible server such as Ollama).</summary>
    public LlmOptions Llm { get; init; } = new();

    /// <summary>Store of per-run quality metrics (disabled unless Program.cs sets one).</summary>
    public RunMetricsStore MetricsStore { get; init; } = RunMetricsStore.Disabled;

    /// <summary>Cache for search responses and screening decisions (disabled unless Program.cs sets one).</summary>
    public ReviewCache Cache { get; init; } = ReviewCache.Disabled;

    // A property, not a field: the engine is created before Program.cs sets AppLog.Factory.
    private static ILogger _log => AppLog.For<PrismaReviewEngine>();

    /// <summary>Creates the review engine with the server's API keys and run settings.</summary>
    /// <param name="mistralApiKey">Mistral API key used for the language-model steps.</param>
    /// <param name="elsevierApiKey">Optional Scopus / ScienceDirect key.</param>
    /// <param name="ieeeApiKey">Optional IEEE Xplore key.</param>
    /// <param name="scholarApiKey">Optional Semantic Scholar API key (the API also works without one).</param>
    /// <param name="supportStatement">Funding and support text for the report; a neutral default is used when empty.</param>
    /// <param name="runsOptions">Concurrency, retention and screening-review settings.</param>
    public PrismaReviewEngine(string mistralApiKey, string elsevierApiKey, string? ieeeApiKey = null, string? scholarApiKey = null, string? supportStatement = null, RunsOptions? runsOptions = null)
    {
        _runsOptions = runsOptions ?? new RunsOptions();
        Coordinator = new RunCoordinator(_runsOptions.MaxConcurrentRuns);
        Store = new RunStore(Path.Join(Directory.GetCurrentDirectory(), "WorkspaceStore"), _runsOptions, Coordinator.IsActive);
        _globalMistralKey = mistralApiKey;
        _globalElsevierKey = elsevierApiKey;
        _globalIeeeKey = ieeeApiKey;
        _globalScholarKey = scholarApiKey;
        _supportStatement = string.IsNullOrWhiteSpace(supportStatement) ? DefaultSupportStatement : supportStatement.Trim();
    }

    /// <summary>Default Mistral model (Llm:Model overrides it for a run; kept for tools/ScreeningEval).</summary>
    public const string ModelId = "mistral-large-latest";

    // Stage names written to ReviewStats.ProcessingStage (and read by the dashboard).
    public const string StageQueued = "Queued";
    public const string StageScreening = "Screening";
    public const string StageAwaitingReview = "AwaitingScreeningReview";
    public const string StageSynthesizing = "Synthesizing";
    public const string StageComplete = "Complete";
    public const string StageFailed = "Failed";
    public const string StageInterrupted = "Interrupted";

    public static bool IsFinishedStage(string? stage) => stage is StageComplete or StageFailed or StageInterrupted;

    /// <summary>True while the run is queued, running or waiting for a screening review in this process.</summary>
    public bool IsRunActive(Guid runId) => Coordinator.IsActive(runId);

    /// <summary>Hands the reviewer's screening decisions to a run that is waiting for them (only with the run's edit key).</summary>
    public bool SubmitScreeningReview(Guid runId, IReadOnlyList<ScreeningOverride> decisions, string? editKey = null) =>
        CanEdit(runId, editKey) && Coordinator.SubmitScreeningReview(runId, decisions);

    public bool IsAwaitingScreeningReview(Guid runId) => Coordinator.IsAwaitingScreeningReview(runId);

    /// <summary>
    /// Runs one review end to end. Model-heavy phases only start when the RunCoordinator hands out a slot,
    /// so at most Runs:MaxConcurrentRuns reviews call the model at the same time; the others show their
    /// place in line. With request.HumanScreeningReview the run pauses after screening until the user has
    /// confirmed or changed the decisions (without holding a slot while it waits).
    /// </summary>
    public async Task RunReviewAsync(Guid sessionId, ReviewRequest request)
    {
        // Every log line written while this run works (engine, sources, cache, model calls) carries its run ID,
        // so a bug report with the ID from the dashboard can be matched to the server log.
        using var logScope = _log.BeginScope(new Dictionary<string, object> { ["RunId"] = sessionId });

        // Every caller gets the same input checks (the dashboard runs them first to show the result): the
        // text fields are normalised, too-long fields stop the run before anything is spent, and
        // instruction-like phrases are recorded in the protocol and the ledger.
        var inputCheck = ReviewInputGuard.Check(request);
        if (!inputCheck.IsValid) throw new ArgumentException(string.Join(" ", inputCheck.Errors));
        request = inputCheck.Request;

        Coordinator.Register(sessionId);
        var (activeMistral, activeElsevier, activeIeee, activeScholar) = ResolveKeys(request.UserKeys);

        // With Llm:FastModel set, the screenings and citation checks go to it (StageRoutedChatService).
        IChatCompletionService innerChat = ChatFactory != null ? ChatFactory(activeMistral) : LlmFactory.CreateForReview(Llm, activeMistral);
        var chat = new RecordingChatCompletionService(innerChat, Llm.Model);

        int yearFrom = request.YearFrom, yearTo = request.YearTo;
        if (yearFrom > 0 && yearTo > 0 && yearFrom > yearTo) (yearFrom, yearTo) = (yearTo, yearFrom);

        // A ticked source without a usable key is not queried, and is recorded as unavailable so the report
        // says so instead of implying it was searched.
        var selectedKeys = (request.SelectedSources == null || request.SelectedSources.Count == 0 ? SourceCatalog.AllKeys : request.SelectedSources)
            .Select(k => SourceCatalog.Find(k)?.Key)
            .Where(k => k != null)
            .Select(k => k!)
            .Distinct()
            .ToList();
        var availability = GetSourceAvailability(request.UserKeys);

        var ctx = new RunContext
        {
            RunId = sessionId,
            Request = request,
            Chat = chat,
            Workspace = GetWorkspaceFolderPath(sessionId),
            State = new ReviewState
            {
                SearchQuery = request.Query,
                PeerReviewOnlyToggle = request.PeerReviewOnly,
                SynthesisTargetDirective = request.SynthesisDirective,
                ReviewObjective = request.Objective,
                ResearchQuestions = request.ResearchQuestions?.ToList() ?? new List<string>(),
                InclusionCriteria = request.Inclusion,
                ExclusionCriteria = request.Exclusion,
                ArtifactKind = ArtifactKinds.Normalize(request.ArtifactKind),
                Method = ReviewMethods.Find(request.Method)?.Key ?? ReviewMethod.SystematicKey,
                SelectedSources = selectedKeys,
                MaxResultsPerSource = request.MaxResultsPerSource,
                YearFrom = yearFrom,
                YearTo = yearTo,
                HumanScreeningReviewRequested = request.HumanScreeningReview,
                DualScreeningRequested = request.DualScreening,
                CitationChainingRequested = request.CitationChaining,
                InputFlags = inputCheck.Flags.Select(f => f.ToString()).ToList(),
                Stats = new ReviewStats
                {
                    RunStartedUtc = DateTime.UtcNow,
                    ProgressPlan = RunProgress.Plan(request.CitationChaining, request.HumanScreeningReview),
                },
            }
        };
        foreach (var key in selectedKeys)
        {
            if (!availability.TryGetValue(key, out bool ok) || !ok) { ctx.State.UnavailableSources.Add(key); continue; }
            ctx.Sources.Add(SourceFactory?.Invoke(key) ?? key switch
            {
                "arxiv" => new ArxivSource(),
                "scopus" => new ElsevierSource(activeElsevier),
                "ieee" => new IeeeXploreSource(activeIeee!),
                "openalex" => new OpenAlexSource(OpenSources.ContactEmail, OpenSources.OpenAlexApiKey),
                "semanticscholar" => new SemanticScholarSource(activeScholar ?? OpenSources.SemanticScholarApiKey),
                "crossref" => new CrossrefSource(OpenSources.ContactEmail),
                _ => throw new InvalidOperationException($"Unknown source key '{key}'.")
            });
        }

        Directory.CreateDirectory(ctx.Workspace);
        var header = new RunHeader(sessionId, ReviewModules.ForCard(ctx.State.Method).Key, ctx.State.Method, ctx.State.Stats.RunStartedUtc ?? DateTime.UtcNow, StageQueued);
        try
        {
            // run.json comes first: it names the module that owns this folder.
            await RunHeader.WriteAsync(ctx.Workspace, header);

            // The protocol is written before anything is searched, so its timestamp and fingerprint predate
            // every result (PRISMA 2020 item 24).
            await WriteProtocolAsync(ctx);

            ctx.State.Stats.ProcessingStage = StageQueued;
            await PublishAsync(ctx);

            using (await AcquireSlotAsync(ctx))
            {
                ctx.State.Stats.ProcessingStage = StageScreening;
                await PublishAsync(ctx);
                await SearchAndScreenAsync(ctx, yearFrom, yearTo);
                if (request.CitationChaining) await ChainCitationsAsync(ctx, yearFrom, yearTo);
                FinishDualScreeningStats(ctx);
                await PublishAsync(ctx);
            }

            if (request.HumanScreeningReview)
            {
                await AwaitScreeningReviewAsync(ctx);
            }

            using (await AcquireSlotAsync(ctx))
            {
                ctx.State.Stats.ProcessingStage = StageSynthesizing;
                await PublishAsync(ctx);
                await RetrieveFullTextsAsync(ctx);
                await SynthesizeAsync(ctx);
            }

            ctx.State.Stats.ProcessingStage = StageComplete;
            ctx.State.Stats.ProgressStep = RunProgress.Done;
            ctx.State.Stats.ProgressFraction = 1;
            ctx.State.Stats.ProgressDetail = "";
            ctx.State.CompletedUtc = DateTime.UtcNow;
            await PublishAsync(ctx);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run {RunId} failed during {Stage}", sessionId, ctx.State.Stats.ProcessingStage);
            ctx.State.Stats.ProcessingStage = StageFailed;
            ctx.State.FailureMessage = $"The run stopped with an error: {SanitizeLogMessage(ex.Message)}";
            ctx.State.CompletedUtc = DateTime.UtcNow;
            try { await PublishAsync(ctx); }
            catch (Exception publishEx)
            {
                _log.LogWarning("Could not save the ledger of the failed run: {Message}", SanitizeLogMessage(publishEx.Message));
            }
            throw;
        }
        finally
        {
            try
            {
                ctx.State.RunSettings = chat.Summarize(RecordingChatCompletionService.AppVersion);
                await SaveStateAsync(sessionId, ctx.State);
                await RunHeader.WriteAsync(ctx.Workspace, header with { Stage = ctx.State.Stats.ProcessingStage, CompletedUtc = ctx.State.CompletedUtc });
                await File.WriteAllTextAsync(Path.Join(ctx.Workspace, "llm-calls.json"),
                    JsonSerializer.Serialize(new { ctx.State.RunSettings, Calls = chat.Calls }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Run {RunId}: could not write llm-calls.json: {Message}", sessionId, ex.Message);
            }
            if (ctx.State.Stats.ProcessingStage == StageComplete)
                await WriteRunMetricsAsync(ctx);
            Coordinator.Unregister(sessionId);
            _lastProgressTicks.TryRemove(sessionId, out _);
            _liveStats.TryRemove(sessionId, out _);
        }
    }
}

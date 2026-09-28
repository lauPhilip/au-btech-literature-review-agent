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

    /// <summary>
    /// The folder of one run. The run id comes from the URL, but as a Guid its "N" form is 32 hex digits, so it
    /// cannot contain a separator or "..". Path.GetFileName and the containment check below make that guarantee
    /// explicit, so the folder can never resolve outside the workspace even if this method is changed later.
    /// </summary>
    private string GetWorkspaceFolderPath(Guid sessionId)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot));
        string folder = Path.GetFullPath(Path.Join(root, Path.GetFileName(sessionId.ToString("N"))));
        if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("A run folder resolved outside the workspace.");
        return folder;
    }

    /// <summary>Folder holding one sub-folder per run. Defaults to ./WorkspaceStore.</summary>
    public string WorkspaceRoot { get; init; } = Path.Join(Directory.GetCurrentDirectory(), "WorkspaceStore");

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
        _globalMistralKey = mistralApiKey;
        _globalElsevierKey = elsevierApiKey;
        _globalIeeeKey = ieeeApiKey;
        _globalScholarKey = scholarApiKey;
        _supportStatement = string.IsNullOrWhiteSpace(supportStatement) ? DefaultSupportStatement : supportStatement.Trim();
    }

    /// <summary>
    /// For each gateway in SourceCatalog: can it be queried with the server keys plus the user's own keys?
    /// Used by the dashboard to grey out sources that have no key.
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetSourceAvailability(UserApiKeys? userKeys)
    {
        var keys = ResolveKeys(userKeys);
        return SourceCatalog.All.ToDictionary(
            s => s.Key,
            s => s.KeyKind switch
            {
                SourceKeyKind.None => true,
                SourceKeyKind.Elsevier => SourceCatalog.IsUsableKey(keys.Elsevier),
                SourceKeyKind.Ieee => SourceCatalog.IsUsableKey(keys.Ieee),
                _ => false
            });
    }

    private (string Mistral, string Elsevier, string? Ieee, string? Scholar) ResolveKeys(UserApiKeys? userKeys) => (
        !string.IsNullOrWhiteSpace(userKeys?.MistralApiKey) ? userKeys.MistralApiKey : _globalMistralKey,
        !string.IsNullOrWhiteSpace(userKeys?.ElsevierApiKey) ? userKeys.ElsevierApiKey : _globalElsevierKey,
        !string.IsNullOrWhiteSpace(userKeys?.IeeeApiKey) ? userKeys.IeeeApiKey : _globalIeeeKey,
        !string.IsNullOrWhiteSpace(userKeys?.ScholarApiKey) ? userKeys.ScholarApiKey : _globalScholarKey);

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

    /// <summary>Hands the reviewer's screening decisions to a run that is waiting for them.</summary>
    public bool SubmitScreeningReview(Guid runId, IReadOnlyList<ScreeningOverride> decisions) =>
        Coordinator.SubmitScreeningReview(runId, decisions);

    public bool IsAwaitingScreeningReview(Guid runId) => Coordinator.IsAwaitingScreeningReview(runId);

    /// <summary>Per-run working data that is not part of the ledger (the full paper records and their text).</summary>
    private sealed class RunContext
    {
        public required Guid RunId { get; init; }
        public required ReviewRequest Request { get; init; }
        public required ReviewState State { get; init; }
        public required RecordingChatCompletionService Chat { get; init; }
        public required string Workspace { get; init; }
        public List<IAcademicSource> Sources { get; } = new();
        public Dictionary<string, AcademicPaper> Papers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<DocumentChunk>> Chunks { get; } = new(StringComparer.Ordinal);

        // Duplicate detection across all sources and citation chaining (source id, DOI, normalised title -> first id seen).
        public Dictionary<string, string> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SeenDois { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SeenTitles { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Screening results of both prompts, for Cohen's kappa.
        public List<(bool First, bool Second)> DualPairs { get; } = new();
    }

    /// <summary>
    /// Runs one review end to end. Model-heavy phases only start when the RunCoordinator hands out a slot,
    /// so at most Runs:MaxConcurrentRuns reviews call the model at the same time; the others show their
    /// place in line. With request.HumanScreeningReview the run pauses after screening until the user has
    /// confirmed or changed the decisions (without holding a slot while it waits).
    /// </summary>
    public async Task RunReviewAsync(Guid sessionId, ReviewRequest request)
    {
        Coordinator.Register(sessionId);
        var (activeMistral, activeElsevier, activeIeee, activeScholar) = ResolveKeys(request.UserKeys);

        IChatCompletionService innerChat = ChatFactory != null ? ChatFactory(activeMistral) : LlmFactory.Create(Llm, activeMistral);
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
                SelectedSources = selectedKeys,
                MaxResultsPerSource = request.MaxResultsPerSource,
                YearFrom = yearFrom,
                YearTo = yearTo,
                HumanScreeningReviewRequested = request.HumanScreeningReview,
                DualScreeningRequested = request.DualScreening,
                CitationChainingRequested = request.CitationChaining,
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
                "openalex" => new OpenAlexSource(OpenSources.ContactEmail),
                "semanticscholar" => new SemanticScholarSource(activeScholar ?? OpenSources.SemanticScholarApiKey),
                "crossref" => new CrossrefSource(OpenSources.ContactEmail),
                _ => throw new InvalidOperationException($"Unknown source key '{key}'.")
            });
        }

        Directory.CreateDirectory(ctx.Workspace);
        try
        {
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
            ctx.State.CompletedUtc = DateTime.UtcNow;
            await PublishAsync(ctx);
        }
        catch (Exception ex)
        {
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
                await File.WriteAllTextAsync(Path.Join(ctx.Workspace, "llm-calls.json"),
                    JsonSerializer.Serialize(new { ctx.State.RunSettings, Calls = chat.Calls }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Run {RunId}: could not write llm-calls.json: {Message}", sessionId, ex.Message);
            }
            Coordinator.Unregister(sessionId);
        }
    }

    private async Task PublishAsync(RunContext ctx)
    {
        await SaveStateAsync(ctx.RunId, ctx.State);
        OnProgressUpdated?.Invoke(ctx.RunId, ctx.State.Stats);
    }

    /// <summary>Waits for a run slot, showing the run's place in line while it waits.</summary>
    private async Task<IDisposable> AcquireSlotAsync(RunContext ctx)
    {
        var acquire = Coordinator.AcquireSlotAsync(ctx.RunId);
        string stageBefore = ctx.State.Stats.ProcessingStage;
        while (!acquire.IsCompleted)
        {
            int position = Coordinator.QueuePosition(ctx.RunId);
            if (position > 0 && (position != ctx.State.Stats.QueuePosition || ctx.State.Stats.ProcessingStage != StageQueued))
            {
                ctx.State.Stats.QueuePosition = position;
                ctx.State.Stats.ProcessingStage = StageQueued;
                await PublishAsync(ctx);
            }
            await Task.WhenAny(acquire, Task.Delay(1000));
        }
        if (ctx.State.Stats.QueuePosition != 0)
        {
            ctx.State.Stats.QueuePosition = 0;
            ctx.State.Stats.ProcessingStage = stageBefore;
        }
        return await acquire;
    }

    /// <summary>
    /// Call once at startup. Runs cannot survive an app restart (IIS recycles the app pool on idle and on a
    /// schedule), so any ledger still in a working stage is marked "Interrupted" instead of spinning forever.
    /// </summary>
    public int MarkInterruptedRuns()
    {
        string store = WorkspaceRoot;
        if (!Directory.Exists(store)) return 0;
        int marked = 0;
        foreach (var dir in Directory.GetDirectories(store))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var runId) || IsRunActive(runId)) continue;
            var state = LoadState(runId);
            if (state == null || IsFinishedStage(state.Stats.ProcessingStage) || state.Stats.ProcessingStage == "Idle") continue;
            state.Stats.ProcessingStage = StageInterrupted;
            state.Stats.QueuePosition = 0;
            state.FailureMessage = "The server restarted while this run was in progress, so it could not finish. Please start it again.";
            state.CompletedUtc = DateTime.UtcNow;
            try { File.WriteAllText(GetStateFilePath(runId), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); marked++; }
            catch (Exception ex)
            {
                _log.LogWarning("Could not mark an interrupted run: {Message}", SanitizeLogMessage(ex.Message));
            }
        }
        return marked;
    }

    /// <summary>Reads a run's ledger from disk, or null if there is none (e.g. expired and deleted).</summary>
    public ReviewState? LoadState(Guid runId)
    {
        string path = GetStateFilePath(runId);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ReviewState>(stream);
        }
        catch
        {
            return null;
        }
    }

    private string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        // Line breaks are removed so a crafted value cannot add fake lines to the log (log forging).
        string oneLine = message.Replace("\r", " ").Replace("\n", " ");
        return Regex.Replace(oneLine, @"([a-zA-Z0-9]{24,64})", "[REDACTED_API_CREDENTIAL]");
    }

    /// <summary>Lower-case title with punctuation and spacing removed, for duplicate detection.</summary>
    public static string NormalizeTitle(string? title) =>
        Regex.Replace(Regex.Replace((title ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " "), @"\s+", " ").Trim();

    /// <summary>Bare lower-case DOI ("10.x/y"), or null when there is no usable DOI.</summary>
    public static string? NormalizeDoi(string? doi)
    {
        if (string.IsNullOrWhiteSpace(doi)) return null;
        string d = Regex.Replace(doi.Trim(), @"^(https?://(dx\.)?doi\.org/|doi:)", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        return Regex.IsMatch(d, @"^10\.\d{4,9}/\S+$") ? d : null;
    }

    /// <summary>Chart category and platform label for a paper, from the prefix of its source id.</summary>
    public static (string Category, string Platform) SourceOfPaper(string paperId) => paperId switch
    {
        _ when paperId.StartsWith("SCOPUS_ID:") => ("ScienceDirect", "Scopus API"),
        _ when paperId.StartsWith("IEEE_") => ("IEEE", "IEEE Xplore API"),
        _ when paperId.StartsWith("OPENALEX_") => ("OpenAlex", "OpenAlex API"),
        _ when paperId.StartsWith("S2_") => ("SemanticScholar", "Semantic Scholar API"),
        _ when paperId.StartsWith("CROSSREF_") => ("Crossref", "Crossref API"),
        _ when paperId.StartsWith("SCHOLAR_") => ("Scholar", "Google Scholar API"),      // runs from before OpenAlex
        _ when paperId.StartsWith("RG_") => ("ResearchGate", "ResearchGate API"),         // runs from before OpenAlex
        _ => ("Arxiv", "arXiv API"),
    };

    /// <summary>Display names for the chart categories used in the report.</summary>
    public static string CategoryLabel(string category) => category switch
    {
        "Arxiv" => "arXiv",
        "ScienceDirect" => "ScienceDirect",
        "IEEE" => "IEEE Xplore",
        "SemanticScholar" => "Semantic Scholar",
        "Scholar" => "Google Scholar",
        _ => category,
    };

    private static string ClassifyVenueFromCitation(string? citation)
    {
        string c = citation ?? "";
        if (c.Contains("Proceedings of", StringComparison.OrdinalIgnoreCase) || c.Contains("Conference", StringComparison.OrdinalIgnoreCase)) return "Conferences";
        if (c.Contains("Transactions on", StringComparison.OrdinalIgnoreCase)) return "Transactions";
        if (c.Contains("Journal of", StringComparison.OrdinalIgnoreCase)) return "Journals";
        return "Preprints";
    }

    private static int ExtractYearFromCitation(string? citation)
    {
        if (string.IsNullOrEmpty(citation)) return 2026;
        var yearMatch = Regex.Match(citation, @"\((20\d{2})\)");
        return yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int yr) ? yr : 2026;
    }

    // Written to a temporary file and moved into place, so a dashboard reading the ledger never sees half a file.
    private async Task SaveStateAsync(Guid sessionId, ReviewState state)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string path = GetStateFilePath(sessionId);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(state, options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes a finished run's folder (ledger, report, source PDFs). Anyone with the run link can open a
    /// run, so its owner must be able to remove it. Refuses while the run is still active.
    /// </summary>
    public bool DeleteRun(Guid runId)
    {
        if (IsRunActive(runId)) return false;
        string folder = GetWorkspaceFolderPath(runId);
        if (!Directory.Exists(folder)) return false;
        try
        {
            Directory.Delete(folder, recursive: true);
            _log.LogInformation("Run {RunId} deleted by its user.", runId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run {RunId} could not be deleted: {Message}", runId, ex.Message);
            return false;
        }
    }

    /// <summary>The run's protocol.md, or null when there is none.</summary>
    public string? ReadProtocol(Guid runId)
    {
        string path = Path.Join(GetWorkspaceFolderPath(runId), "protocol.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Writes protocol.md before the search starts and records its fingerprint in the ledger.</summary>
    private async Task WriteProtocolAsync(RunContext ctx)
    {
        var names = ctx.State.SelectedSources.Except(ctx.State.UnavailableSources).Select(SourceCatalog.DisplayNameFor).ToList();
        string text = ProtocolWriter.Write(ctx.RunId, ctx.Request with { UserKeys = null }, names, Llm.DisplayName, _runsOptions, DateTime.UtcNow);
        string path = Path.Join(ctx.Workspace, "protocol.md");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        ctx.State.ProtocolSha256 = RunManifest.Sha256(File.ReadAllBytes(path));
    }
}

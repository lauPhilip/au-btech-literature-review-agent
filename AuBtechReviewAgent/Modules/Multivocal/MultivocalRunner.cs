using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>What the runner is doing for one run: the step it is on, its last progress line, and why it stopped.</summary>
public sealed record MultivocalRunStatus(bool Running, string Step, string? Progress, string? Error);

/// <summary>
/// Runs a planned multivocal review from where it stands to the synthesis, without a click between the steps, as the
/// systematic review runs (MLR block H): search, screening, quality, the map (the model's proposal is fixed as it
/// is), extraction and synthesis. Each step is the module's own service, so every step keeps its checks and writes
/// its files as before; the runner only decides which step comes next from the run's stage. It runs on the server,
/// so closing the page does not stop it. A step that fails stops the run where it is; starting it again continues
/// from that step, and decisions already made come from the cache.
/// </summary>
public sealed class MultivocalRunner
{
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalRunner");

    private readonly RunStore _runs;
    private readonly MultivocalSearcher _searcher;
    private readonly MultivocalScreener _screener;
    private readonly MultivocalQualityAssessor _quality;
    private readonly MultivocalMapper _mapper;
    private readonly MultivocalExtractor _extractor;
    private readonly MultivocalSynthesiser _synthesiser;
    private readonly ConcurrentDictionary<Guid, MultivocalRunStatus> _status = new();

    /// <summary>Raised whenever a run's status changes, with the run's id; pages listen to show the progress.</summary>
    public event Action<Guid>? Changed;

    public MultivocalRunner(RunStore runs, MultivocalSearcher searcher, MultivocalScreener screener, MultivocalQualityAssessor quality,
        MultivocalMapper mapper, MultivocalExtractor extractor, MultivocalSynthesiser synthesiser)
    {
        _runs = runs;
        _searcher = searcher;
        _screener = screener;
        _quality = quality;
        _mapper = mapper;
        _extractor = extractor;
        _synthesiser = synthesiser;
    }

    /// <summary>The runner's status for a run on this server, or null when it has not run here since the server started.</summary>
    public MultivocalRunStatus? StatusOf(Guid runId) => _status.TryGetValue(runId, out var status) ? status : null;

    public bool IsRunning(Guid runId) => StatusOf(runId)?.Running == true;

    /// <summary>Whether a run at this stage has steps left for the runner.</summary>
    public static bool HasStepsLeft(string? stage) => stage is not (null or "" or MultivocalSynthesiser.StageSynthesised);

    /// <summary>
    /// Starts the run in the background and returns at once; false when it is running already or the key does not
    /// fit. <see cref="RunAsync"/> does the work; tests call it directly.
    /// </summary>
    public bool Start(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) return false;
        if (!TrySet(runId, new MultivocalRunStatus(true, "Starting", null, null))) return false;
        _ = Task.Run(() => RunStepsAsync(runId, editKey));
        return true;
    }

    /// <summary>Runs every step left, in this call. Throws when the run is running already or the key does not fit.</summary>
    public async Task RunAsync(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can run it.");
        if (!TrySet(runId, new MultivocalRunStatus(true, "Starting", null, null))) throw new InvalidOperationException("This run is running already.");
        await RunStepsAsync(runId, editKey);
    }

    private bool TrySet(Guid runId, MultivocalRunStatus status)
    {
        while (true)
        {
            if (_status.TryGetValue(runId, out var current))
            {
                if (current.Running) return false;
                if (_status.TryUpdate(runId, status, current)) break;
            }
            else if (_status.TryAdd(runId, status)) break;
        }
        Changed?.Invoke(runId);
        return true;
    }

    private void Set(Guid runId, MultivocalRunStatus status)
    {
        _status[runId] = status;
        try { Changed?.Invoke(runId); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A page that has gone away must not stop the run.
            _log.LogDebug("A listener failed: {Message}", ex.Message);
        }
    }

    private async Task RunStepsAsync(Guid runId, string? editKey)
    {
        string step = "Starting";
        try
        {
            // Each pass runs the step the stage asks for; a step moves the stage on, so the loop ends at the synthesis.
            // The bound only guards against a stage that does not move.
            for (int guard = 0; guard < 12; guard++)
            {
                string stage = _runs.LoadHeader(runId)?.Stage ?? throw new InvalidOperationException("This run could not be found.");
                if (!HasStepsLeft(stage)) break;
                step = StepFor(stage);
                Set(runId, new MultivocalRunStatus(true, step, null, null));
                var progress = new Progress<string>(line => Set(runId, new MultivocalRunStatus(true, step, line, null)));
                switch (stage)
                {
                    case MultivocalPlanner.StagePlanned:
                    case MultivocalSearcher.StageSearching:
                        if (stage == MultivocalSearcher.StageSearching) await ResetAsync(runId, MultivocalPlanner.StagePlanned); // a search stopped by the server starts again
                        await _searcher.SearchAsync(runId, editKey, progress);
                        break;
                    case MultivocalSearcher.StageSearched:
                    case MultivocalScreener.StageScreening:
                        await _screener.ScreenAsync(runId, editKey, progress);
                        break;
                    case MultivocalScreener.StageScreened:
                    case MultivocalQualityAssessor.StageAssessing:
                        await _quality.AssessAsync(runId, editKey, progress);
                        break;
                    case MultivocalQualityAssessor.StageAssessed:
                        if (_quality.Load(runId)?.Count("Passed") is null or 0)
                        {
                            Set(runId, new MultivocalRunStatus(false, "Stopped", null, "No source passed the quality check, so there is nothing to map or synthesise."));
                            return;
                        }
                        await _mapper.ProposeAsync(runId, editKey);
                        break;
                    case MultivocalMapper.StageMapping:
                        await _mapper.FixAsync(runId, editKey); // the model's map is used as proposed
                        break;
                    case MultivocalMapper.StageMapped:
                    case MultivocalExtractor.StageExtracting:
                        await _extractor.ExtractAsync(runId, editKey, progress);
                        break;
                    case MultivocalExtractor.StageExtracted:
                        await _synthesiser.SynthesiseAsync(runId, editKey, progress);
                        break;
                    default:
                        throw new InvalidOperationException($"The run is at a stage the runner does not know: {stage}.");
                }
            }
            Set(runId, new MultivocalRunStatus(false, "Done", null, null));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or LlmOutputException or HttpRequestException or TaskCanceledException)
        {
            string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.LogWarning("Multivocal run {RunId} stopped at {Step}: {Message}", runId, step, message);
            Set(runId, new MultivocalRunStatus(false, step, null, $"{step} stopped: {message}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Multivocal run {RunId} failed at {Step}", runId, step);
            Set(runId, new MultivocalRunStatus(false, step, null, $"{step} stopped because of an error on the server. Start it again to continue from this step."));
        }
    }

    private async Task ResetAsync(Guid runId, string stage)
    {
        if (_runs.LoadHeader(runId) is { } header)
            await RunHeader.WriteAsync(_runs.FolderOf(runId), header with { Stage = stage });
    }

    /// <summary>The step the runner takes at a stage, in the words the page shows.</summary>
    public static string StepFor(string stage) => stage switch
    {
        MultivocalPlanner.StagePlanned or MultivocalSearcher.StageSearching => "Searching",
        MultivocalSearcher.StageSearched or MultivocalScreener.StageScreening => "Screening",
        MultivocalScreener.StageScreened or MultivocalQualityAssessor.StageAssessing => "Scoring the quality",
        MultivocalQualityAssessor.StageAssessed => "Proposing the map",
        MultivocalMapper.StageMapping => "Fixing the map",
        MultivocalMapper.StageMapped or MultivocalExtractor.StageExtracting => "Extracting",
        MultivocalExtractor.StageExtracted => "Synthesising",
        _ => "Done",
    };
}

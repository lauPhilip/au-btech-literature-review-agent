using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// What the runner is doing for one run: the step it is on, its last progress line, why it stopped, and when this
/// start of the run began and ended (for the elapsed time on the page).
/// </summary>
public sealed record MultivocalRunStatus(bool Running, string Step, string? Progress, string? Error, DateTime? StartedUtc = null, DateTime? EndedUtc = null);

/// <summary>
/// Runs a planned multivocal review from where it stands to the synthesis, without a click between the steps, as the
/// systematic review runs (MLR block H): search, screening, quality, the map (the model's proposal is fixed as it
/// is), extraction, synthesis, and then the writing and the citation check of the paper (MultivocalWriter). Each step is the module's own service, so every step keeps its checks and writes
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
    private readonly MultivocalPages _pages;
    private readonly MultivocalWriter? _writer;
    private readonly ConcurrentDictionary<Guid, MultivocalRunStatus> _status = new();

    /// <summary>Raised whenever a run's status changes, with the run's id; pages listen to show the progress.</summary>
    public event Action<Guid>? Changed;

    public MultivocalRunner(RunStore runs, MultivocalSearcher searcher, MultivocalScreener screener, MultivocalQualityAssessor quality,
        MultivocalMapper mapper, MultivocalExtractor extractor, MultivocalSynthesiser synthesiser, MultivocalPages pages, MultivocalWriter? writer = null)
    {
        _pages = pages;
        _writer = writer;
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
    public static bool HasStepsLeft(string? stage) => stage is not (null or "" or MultivocalWriter.StageChecked);

    /// <summary>
    /// Starts the run in the background and returns at once; false when it is running already or the key does not
    /// fit. <see cref="RunAsync"/> does the work; tests call it directly.
    /// </summary>
    public bool Start(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) return false;
        if (!TrySet(runId, new MultivocalRunStatus(true, "Starting", null, null, DateTime.UtcNow))) return false;
        _ = Task.Run(() => RunStepsAsync(runId, editKey));
        return true;
    }

    /// <summary>Runs every step left, in this call. Throws when the run is running already or the key does not fit.</summary>
    public async Task RunAsync(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can run it.");
        if (!TrySet(runId, new MultivocalRunStatus(true, "Starting", null, null, DateTime.UtcNow))) throw new InvalidOperationException("This run is running already.");
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
        var started = _status.TryGetValue(runId, out var current) ? current.StartedUtc : null;
        _status[runId] = status with { StartedUtc = started, EndedUtc = status.Running ? null : DateTime.UtcNow };
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
                    case MultivocalSynthesiser.StageSynthesised:
                    case MultivocalWriter.StageWriting:
                        // The references are written in code first; the paper cites them by their numbers.
                        await SaveReferencesAsync(runId);
                        if (_writer == null) { Set(runId, new MultivocalRunStatus(false, "Done", null, null)); return; }
                        await _writer.WriteAsync(runId, editKey, progress);
                        break;
                    case MultivocalWriter.StageWritten:
                    case MultivocalWriter.StageChecking:
                        if (_writer == null) { Set(runId, new MultivocalRunStatus(false, "Done", null, null)); return; }
                        await _writer.CheckAsync(runId, editKey, progress);
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

    /// <summary>Writes references.bib, references.ris and screened.ris to the run folder, in code (G-6).</summary>
    private async Task SaveReferencesAsync(Guid runId)
    {
        var ledger = _searcher.LoadLedger(runId);
        var screening = _screener.Load(runId);
        if (ledger == null || screening == null) return;
        await GreyReferences.SaveAsync(_runs.FolderOf(runId), ledger, _pages.Load(runId), screening, _extractor.Load(runId));
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
        MultivocalSynthesiser.StageSynthesised or MultivocalWriter.StageWriting => "Writing the paper",
        MultivocalWriter.StageWritten or MultivocalWriter.StageChecking => "Checking the citations",
        _ => "Done",
    };
}

/// <summary>
/// The steps of a multivocal run as the progress panel shows them, how far a run is, and the state of each step,
/// worked out from the run's stage and the runner's progress line ("12 of 30 sources screened"). The weights are a
/// rough share of the run's time: the quality step fetches every page and is the longest.
/// </summary>
public static class MultivocalProgress
{
    public sealed record ProgressStep(string Key, string Label, int Weight);

    public static readonly IReadOnlyList<ProgressStep> Steps = new[]
    {
        new ProgressStep("search", "Search", 5),
        new ProgressStep("screening", "Screening", 20),
        new ProgressStep("quality", "Quality", 25),
        new ProgressStep("map", "Map", 5),
        new ProgressStep("extraction", "Extraction", 15),
        new ProgressStep("synthesis", "Synthesis", 10),
        new ProgressStep("writing", "Writing", 10),
        new ProgressStep("checking", "Checking", 10),
    };

    /// <summary>The index of the step a run at this stage is on; <see cref="Steps"/>.Count when every step is done.</summary>
    public static int CurrentIndex(string? stage) => stage switch
    {
        MultivocalSearcher.StageSearched or MultivocalScreener.StageScreening => 1,
        MultivocalScreener.StageScreened or MultivocalQualityAssessor.StageAssessing => 2,
        MultivocalQualityAssessor.StageAssessed or MultivocalMapper.StageMapping => 3,
        MultivocalMapper.StageMapped or MultivocalExtractor.StageExtracting => 4,
        MultivocalExtractor.StageExtracted => 5,
        MultivocalSynthesiser.StageSynthesised or MultivocalWriter.StageWriting => 6,
        MultivocalWriter.StageWritten or MultivocalWriter.StageChecking => 7,
        MultivocalWriter.StageChecked => Steps.Count,
        _ => 0,
    };

    /// <summary>The share of a step done, from a progress line such as "12 of 30 sources screened"; 0 without one.</summary>
    public static double Fraction(string? progress)
    {
        var m = System.Text.RegularExpressions.Regex.Match(progress ?? "", @"(\d+) of (\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int done) || !int.TryParse(m.Groups[2].Value, out int all) || all <= 0) return 0;
        return Math.Clamp((double)done / all, 0, 1);
    }

    /// <summary>How far the run is, 0 to 100.</summary>
    public static int Percent(string? stage, string? progress)
    {
        int current = CurrentIndex(stage);
        if (current >= Steps.Count) return 100;
        double done = Steps.Take(current).Sum(s => s.Weight) + Steps[current].Weight * Fraction(progress);
        return (int)Math.Round(done * 100 / Steps.Sum(s => s.Weight));
    }

    /// <summary>"done", "current" or "waiting" for one step.</summary>
    public static string StateOf(int index, string? stage)
    {
        int current = CurrentIndex(stage);
        return index < current ? "done" : index == current ? "current" : "waiting";
    }
}

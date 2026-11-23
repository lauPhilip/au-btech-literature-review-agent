using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// Scores the included grey sources of a screened multivocal run on the quality checklist (MLR block E, G11, Table 7).
/// For each source the page text is kept first (<see cref="MultivocalPages"/>); the model answers the 17 items that
/// need reading, each point backed by a quote, and a quote that is not in the kept text gives no points. The date,
/// the impact and the outlet type are decided in code (<see cref="GreyQualityChecklist"/>). A source at or above the
/// plan's threshold passes; one below it is excluded for quality with its score; one whose page cannot be read is
/// not assessed and says why. The run goes from "Screened" through "Assessing" to "Assessed".
/// </summary>
public sealed partial class MultivocalQualityAssessor
{
    public const string QualityFile = "multivocal-quality.json";
    public const string StageAssessing = "Assessing";
    public const string StageAssessed = "Assessed";

    /// <summary>Part of the cache key: raise it whenever the quality prompt changes.</summary>
    public const string PromptVersion = "grey-quality-v4";

    /// <summary>At most this much of a page is sent to the extraction; a quote must come from this part.</summary>
    public const int MaxTextForModel = 40_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalQualityAssessor");

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly MultivocalScreener _screener;
    private readonly MultivocalPages _pages;
    private readonly Func<IChatCompletionService> _chat;
    private readonly ReviewCache _cache;
    private readonly string _model;
    private readonly int _parallelism;

    /// <summary>An assessor that keeps pages through <paramref name="pages"/> and asks <paramref name="chat"/>'s model.</summary>
    public MultivocalQualityAssessor(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, MultivocalScreener screener,
        MultivocalPages pages, Func<IChatCompletionService> chat, ReviewCache? cache = null, string model = "", int parallelism = 4)
    {
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _screener = screener;
        _pages = pages;
        _chat = chat;
        _cache = cache ?? ReviewCache.Disabled;
        _model = model;
        _parallelism = Math.Max(1, parallelism);
    }

    public MultivocalQualityFile? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), QualityFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalQualityFile>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>
    /// Assesses one source again whose quality could not be assessed (its page could not be read, or the model call
    /// failed), so one failure does not mean planning the run again. A source that was assessed keeps its score.
    /// </summary>
    public async Task<GreyQuality> RetryAsync(Guid runId, string? editKey, string address)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can assess its sources.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        var record = _searcher.LoadLedger(runId)?.Sources.Select(s => s.Record).FirstOrDefault(r => MultivocalSearcher.AddressKey(r.Url) == address)
            ?? throw new InvalidOperationException("This source was not found in this run.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run is being assessed already.");
        try
        {
            var file = Load(runId) ?? throw new InvalidOperationException("Score the quality first.");
            int index = file.Sources.FindIndex(q => q.Address == address);
            if (index < 0) throw new InvalidOperationException("This source was not assessed in this run.");
            if (file.Sources[index].Outcome != "Not assessed") throw new InvalidOperationException("This source has a score already.");

            var again = await AssessOneAsync(runId, editKey, _chat(), planned.Plan, record);
            file.Sources[index] = again;
            await SafeFile.WriteAllTextAsync(Path.Join(_runs.FolderOf(runId), QualityFile), JsonSerializer.Serialize(file, Json));
            return again;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    /// <summary>Assesses every included source of a screened run. Needs the run's edit key.</summary>
    public async Task<MultivocalQualityFile> AssessAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can assess its sources.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        var ledger = _searcher.LoadLedger(runId) ?? throw new InvalidOperationException("Search the run first.");
        var screening = _screener.Load(runId) ?? throw new InvalidOperationException("Screen the sources first.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run is being assessed already.");

        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage is not (MultivocalScreener.StageScreened or StageAssessing))
                throw new InvalidOperationException("This run has been assessed already.");
            await RunHeader.WriteAsync(folder, header with { Stage = StageAssessing });

            var records = ledger.Sources.ToDictionary(s => MultivocalSearcher.AddressKey(s.Record.Url), s => s.Record);
            var included = screening.Sources.Where(s => s.Decision == "Included" && records.ContainsKey(s.Address)).ToList();
            var file = new MultivocalQualityFile
            {
                RunId = runId,
                AssessedUtc = DateTime.UtcNow,
                PromptVersion = PromptVersion,
                Model = _model,
                Threshold = planned.Plan.QualityThreshold,
                FormalAppraisalVersion = planned.Plan.UsesFormalPool ? FormalAppraisalVersion : null,
            };

            var chat = _chat();
            using var throttle = new SemaphoreSlim(_parallelism);
            var work = included.Select(async s =>
            {
                await throttle.WaitAsync();
                try { return await AssessOneAsync(runId, editKey, chat, planned.Plan, records[s.Address]); }
                finally { throttle.Release(); }
            }).ToList();
            for (int i = 0; i < work.Count; i++)
            {
                file.Sources.Add(await work[i]);
                progress?.Report($"{i + 1} of {work.Count} included sources assessed");
            }

            await SafeFile.WriteAllTextAsync(Path.Join(folder, QualityFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageAssessed });
            return file;
        }
        catch
        {
            if (_runs.LoadHeader(runId) is { Stage: StageAssessing } current)
                await RunHeader.WriteAsync(folder, current with { Stage = MultivocalScreener.StageScreened });
            throw;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    private async Task<GreyQuality> AssessOneAsync(Guid runId, string? editKey, IChatCompletionService chat, MultivocalPlan plan, GreyRecord record)
    {
        if (record.Kind == MultivocalSearcher.FormalKind) return await AppraiseFormalAsync(runId, editKey, chat, record);
        var result = new GreyQuality { Address = MultivocalSearcher.AddressKey(record.Url), Title = record.Title, Url = record.Url };

        // The page text first: a kept snapshot, or keep it now.
        string? text = null;
        var snapshot = _pages.Load(runId).Pages.FirstOrDefault(p => MultivocalSearcher.AddressKey(p.Url) == result.Address);
        if (snapshot is not { NotKept: null })
        {
            try { snapshot = await _pages.KeepAsync(runId, editKey, record.Url); }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                result.Outcome = "Not assessed";
                result.NotAssessed = $"the page could not be fetched ({ex.Message.Replace('\r', ' ').Replace('\n', ' ')})";
                return result;
            }
        }
        if (snapshot.NotKept == null) text = _pages.ReadText(runId, snapshot);
        if (text == null)
        {
            result.Outcome = "Not assessed";
            result.NotAssessed = snapshot.NotKept ?? "the kept page text no longer matches its fingerprint";
            return result;
        }
        result.TextSha256 = snapshot.TextSha256;

        // A long page is sent as its start, its end and the passages that show the items (GreyPageText); every quote is checked against what was sent.
        var sent = GreyPageText.ForQuality(text, TopicLine(plan));
        result.Passages = sent.Passages;
        string forModel = sent.Text;
        try
        {
            var (answers, fromCache) = await AnswerCachedAsync(chat, plan, record, forModel, snapshot.TextSha256, sent.Passages != null);
            result.FromCache = fromCache;
            result.Answers = Score(record, answers.Answers, forModel);
            result.Argument = Argument(answers.Opinion, forModel);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.LogWarning("Quality assessment failed for '{Title}': {Message}", record.Title.Replace('\r', ' ').Replace('\n', ' '), message);
            result.Outcome = "Not assessed";
            result.NotAssessed = $"the model call failed ({message})";
            return result;
        }
        result.Points = result.Answers.Sum(a => a.Score);
        result.Outcome = OutcomeFor(result.Points, plan.QualityThreshold);
        return result;
    }
}

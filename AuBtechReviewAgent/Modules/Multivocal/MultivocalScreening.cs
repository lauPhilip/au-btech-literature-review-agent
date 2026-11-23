using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// Screens the grey sources of a searched multivocal run (MLR block E, G9–G10): every source twice, independently, with
/// the plan's criteria and the same steps and answer format as the systematic review's screening
/// (<see cref="PrismaReviewEngine.ScreeningProcedure"/>), so formal and grey sources get the same care. A source the two
/// screenings disagree on is kept and flagged, as in the systematic review. Sources with the same title under another
/// address (an OpenAlex work with several DOIs, seen in a live run) are screened once. The run goes from "Searched"
/// through "Screening" to "Screened".
/// </summary>
public sealed partial class MultivocalScreener
{
    public const string ScreeningFile = "multivocal-screening.json";
    public const string StageScreening = "Screening";
    public const string StageScreened = "Screened";

    /// <summary>Part of the cache key: raise it whenever the grey screening prompt changes.</summary>
    public const string PromptVersion = "grey-screening-v1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();
    private static readonly SemaphoreSlim ReviewGate = new(1, 1);
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalScreener");

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly Func<IChatCompletionService> _chat;
    private readonly ReviewCache _cache;
    private readonly Func<string, string> _modelFor;
    private readonly int _parallelism;

    /// <summary>
    /// A screener that asks <paramref name="chat"/>'s model, several sources at a time. <paramref name="modelFor"/> names
    /// the model that answers a stage ("screening", "screening-second") when a fast model takes them (Llm:FastModel);
    /// without it, <paramref name="model"/> answers both.
    /// </summary>
    public MultivocalScreener(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, Func<IChatCompletionService> chat,
        ReviewCache? cache = null, string model = "", int parallelism = 4, Func<string, string>? modelFor = null)
    {
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _chat = chat;
        _cache = cache ?? ReviewCache.Disabled;
        _modelFor = modelFor ?? (_ => model);
        _parallelism = Math.Max(1, parallelism);
    }

    public MultivocalScreeningFile? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), ScreeningFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalScreeningFile>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The reviewer's decision on one source, as in the systematic review's human review step: it confirms or changes
    /// the model's decision, with an optional note, and the model's decision stays on file. Possible only while the
    /// run is screened and its quality not yet scored, since the quality step uses the included sources.
    /// </summary>
    public async Task<GreyScreening> ReviewAsync(Guid runId, string? editKey, string address, bool include, string? note)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can change its decisions.");
        await ReviewGate.WaitAsync();
        try
        {
            if (_runs.LoadHeader(runId)?.Stage != StageScreened)
                throw new InvalidOperationException("Decisions can be changed only after screening and before the quality is scored.");
            var file = Load(runId) ?? throw new InvalidOperationException("This run has not been screened.");
            var entry = file.Sources.FirstOrDefault(s => s.Address == address) ?? throw new InvalidOperationException("This source was not screened in this run.");
            if (entry.Decision == "Duplicate") throw new InvalidOperationException("A duplicate follows the source it duplicates.");

            entry.ModelDecision ??= entry.Decision;
            entry.Decision = include ? "Included" : "Excluded";
            entry.ExclusionReason = include ? null : ExclusionReasons.ByReviewer;
            entry.Reviewed = true;
            entry.ReviewerNote = string.IsNullOrWhiteSpace(note) ? null : ReviewInputGuard.Normalize(note, singleLine: true);
            entry.ReviewedUtc = DateTime.UtcNow;
            await SafeFile.WriteAllTextAsync(Path.Join(_runs.FolderOf(runId), ScreeningFile), JsonSerializer.Serialize(file, Json));
            return entry;
        }
        finally
        {
            ReviewGate.Release();
        }
    }

    /// <summary>Whether this server is screening the run right now.</summary>
    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>
    /// Screens every found source of a searched run. Needs the run's edit key. A run left at "Screening" by a stopped
    /// server can be screened again; decisions already made come from the cache.
    /// </summary>
    public async Task<MultivocalScreeningFile> ScreenAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can screen it.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        if (!planned.Plan.HasCriteria) throw new InvalidOperationException("This plan has no inclusion criteria, so its sources cannot be screened. Plan a new run.");
        var ledger = _searcher.LoadLedger(runId) ?? throw new InvalidOperationException("Search the run first.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run is being screened already.");

        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage is not (MultivocalSearcher.StageSearched or StageScreening))
                throw new InvalidOperationException("This run has been screened already.");
            await RunHeader.WriteAsync(folder, header with { Stage = StageScreening });

            var file = await ScreenAllAsync(runId, planned.Plan, ledger, progress);

            await SafeFile.WriteAllTextAsync(Path.Join(folder, ScreeningFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageScreened });
            return file;
        }
        catch
        {
            // A failed screening leaves the run searched, so it can be tried again.
            if (_runs.LoadHeader(runId) is { Stage: StageScreening } current)
                await RunHeader.WriteAsync(folder, current with { Stage = MultivocalSearcher.StageSearched });
            throw;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    private async Task<MultivocalScreeningFile> ScreenAllAsync(Guid runId, MultivocalPlan plan, MultivocalLedger ledger, IProgress<string>? progress)
    {
        var file = new MultivocalScreeningFile
        {
            RunId = runId,
            ScreenedUtc = DateTime.UtcNow,
            PromptVersion = PromptVersion,
            Model = _modelFor(FirstStage),
            InclusionCriteria = plan.InclusionCriteria,
            ExclusionCriteria = plan.ExclusionCriteria,
            FormalPromptVersion = plan.UsesFormalPool ? FormalPromptVersion : null,
        };

        // The same title by the same producer under another address is screened once; the copies point to it.
        var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);
        var toScreen = new List<(int Index, GreyRecord Record)>();
        foreach (var found in ledger.Sources)
        {
            var r = found.Record;
            var entry = new GreyScreening { Address = MultivocalSearcher.AddressKey(r.Url), Title = r.Title, Url = r.Url, Formal = found.IsFormal };
            string title = SameSourceKey(r);
            if (title.Length > 0 && byTitle.TryGetValue(title, out var first))
            {
                entry.Decision = "Duplicate";
                entry.DuplicateOf = first;
                entry.Reasoning = r.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= DistinctTitleWords
                    ? "The same title was found under another address; that source was screened."
                    : "The same title by the same producer was found under another address; that source was screened.";
            }
            else
            {
                if (title.Length > 0) byTitle[title] = entry.Address;
                toScreen.Add((file.Sources.Count, r));
            }
            file.Sources.Add(entry);
        }

        var chat = _chat();
        using var throttle = new SemaphoreSlim(_parallelism);
        var work = toScreen.Select(async item =>
        {
            await throttle.WaitAsync();
            try { return await ScreenOneAsync(chat, plan, item.Record); }
            finally { throttle.Release(); }
        }).ToList();

        var pairs = new List<(bool Formal, bool First, bool Second)>();
        for (int i = 0; i < work.Count; i++)
        {
            var screened = await work[i];
            var entry = file.Sources[toScreen[i].Index];
            Apply(entry, screened, toScreen[i].Record);
            if (screened.First != null && screened.Second != null) pairs.Add((entry.Formal, screened.First.IsIncluded, screened.Second.IsIncluded));
            progress?.Report($"{i + 1} of {work.Count} sources screened");
        }
        file.Kappa = Kappa(pairs);
        if (plan.UsesFormalPool)
        {
            // Each pool's agreement apart as well, since the two pools are screened from different kinds of record (G10).
            file.KappaGrey = Kappa(pairs.Where(p => !p.Formal));
            file.KappaFormal = Kappa(pairs.Where(p => p.Formal));
        }
        return file;
    }

    private static double? Kappa(IEnumerable<(bool Formal, bool First, bool Second)> pairs)
    {
        var list = pairs.Select(p => (p.First, p.Second)).ToList();
        return list.Count == 0 ? null : Math.Round(ScreeningConfusion.From(list).CohensKappa, 3);
    }
}

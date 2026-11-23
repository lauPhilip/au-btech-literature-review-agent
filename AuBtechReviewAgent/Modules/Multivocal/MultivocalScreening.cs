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
/// Screens the sources of a searched multivocal run (MLR block E, G9–G10): every source once, and a second time,
/// independently, when <see cref="MultivocalSecondScreening"/> picks it (included, unsure or flagged, or in the random
/// audit sample), with the plan's criteria and the systematic review's answer format and second screener's steps
/// (<see cref="PrismaReviewEngine.ScreeningProcedure"/>), and first steps that ask for positive evidence
/// (<see cref="FirstProcedure"/>), so formal and grey sources get the same care. A source the two
/// screenings disagree on is kept and flagged, as in the systematic review. Sources with the same title under another
/// address (an OpenAlex work with several DOIs, seen in a live run) are screened once. The run goes from "Searched"
/// through "Screening" to "Screened".
/// </summary>
public sealed partial class MultivocalScreener
{
    public const string ScreeningFile = "multivocal-screening.json";
    public const string StageScreening = "Screening";
    public const string StageScreened = "Screened";

    /// <summary>
    /// Part of the cache key: raise it whenever the grey screening prompt changes. v2: second screenings are targeted
    /// (<see cref="MultivocalSecondScreening"/>), so no answer cached under the rule of two screenings each is reused.
    /// v3: the first screening looks for positive evidence (<see cref="FirstProcedure"/>).
    /// </summary>
    public const string PromptVersion = "grey-screening-v3";

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

        // First pass: every source once.
        var chat = _chat();
        var firsts = await InParallelAsync(toScreen, item => ScreenFirstAsync(chat, plan, item.Record), (i, n) => progress?.Report($"{i} of {n} sources screened"));

        // The pilot check: more than half of a pool's first 30 sources included is a warning on file, never a stop.
        var decided = toScreen.Select((item, i) => (item, first: firsts[i])).Where(x => x.first.Answer != null).ToList();
        foreach (bool formal in new[] { false, true })
            if (MultivocalPilotCheck.Warning(decided.Where(x => file.Sources[x.item.Index].Formal == formal).Select(x => x.first.Answer!.IsIncluded), formal) is { } warning)
                (file.Warnings ??= new()).Add(warning);

        // Second pass: only the sources the rule picks (MultivocalSecondScreening), each pool's audit sample drawn from
        // every source the first pass decided, whatever it decided.
        file.AuditSeed = MultivocalSecondScreening.SeedOf(runId);
        file.AuditShare = MultivocalSecondScreening.AuditShare;
        file.AuditMinimum = MultivocalSecondScreening.AuditMinimum;
        var audit = new HashSet<string>(StringComparer.Ordinal);
        foreach (bool formal in new[] { false, true })
            audit.UnionWith(MultivocalSecondScreening.AuditSample(file.AuditSeed,
                decided.Where(x => file.Sources[x.item.Index].Formal == formal).Select(x => file.Sources[x.item.Index].Address)));
        var again = new List<(int Index, GreyRecord Record)>();
        foreach (var (item, first) in decided)
        {
            var entry = file.Sources[item.Index];
            entry.SecondReason = MultivocalSecondScreening.TargetReason(first.Answer!, item.Record.Summary.Length > 0, first.Flags.Count > 0);
            entry.AuditSample = audit.Contains(entry.Address);
            if (entry.SecondReason != null || entry.AuditSample) again.Add(item);
        }
        var seconds = await InParallelAsync(again, item => ScreenSecondAsync(chat, plan, item.Record), (i, n) => progress?.Report($"{i} of {n} second screenings"));
        var secondOf = again.Select((item, i) => (item.Index, seconds[i])).ToDictionary(x => x.Index, x => x.Item2);

        for (int i = 0; i < toScreen.Count; i++)
        {
            var (index, record) = toScreen[i];
            Apply(file.Sources[index], firsts[i], secondOf.TryGetValue(index, out var second) ? second : null, record);
        }

        // κ over the audit sample only: the targeted second screenings are reported apart, as counts.
        var sample = file.Sources.Where(s => s.AuditSample && s.Decision != "Error").ToList();
        file.Kappa = MultivocalSecondScreening.Kappa(sample);
        if (plan.UsesFormalPool)
        {
            // Each pool's agreement apart as well, since the two pools are screened from different kinds of record (G10).
            file.KappaGrey = MultivocalSecondScreening.Kappa(sample.Where(s => !s.Formal));
            file.KappaFormal = MultivocalSecondScreening.Kappa(sample.Where(s => s.Formal));
        }
        return file;
    }

    private async Task<List<T>> InParallelAsync<T>(List<(int Index, GreyRecord Record)> items, Func<(int Index, GreyRecord Record), Task<T>> screen, Action<int, int> report)
    {
        using var throttle = new SemaphoreSlim(_parallelism);
        var work = items.Select(async item =>
        {
            await throttle.WaitAsync();
            try { return await screen(item); }
            finally { throttle.Release(); }
        }).ToList();
        var results = new List<T>();
        for (int i = 0; i < work.Count; i++)
        {
            results.Add(await work[i]);
            report(i + 1, work.Count);
        }
        return results;
    }
}

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

/// <summary>The screening of one found grey source.</summary>
public sealed class GreyScreening
{
    /// <summary>The source's address key (<see cref="MultivocalSearcher.AddressKey"/>), which links it to the ledger.</summary>
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>"Included", "Excluded", "Duplicate" (the same title found under another address) or "Error".</summary>
    public string Decision { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string? Summary { get; set; }
    public string? ExclusionReason { get; set; }
    public string? Confidence { get; set; }

    /// <summary>The two screenings disagreed, the confidence is low or the text looked like instructions: the reviewer should look.</summary>
    public bool Uncertain { get; set; }

    /// <summary>For a duplicate: the address of the source with the same title and producer that was screened.</summary>
    public string? DuplicateOf { get; set; }
    public ScreeningAnswer? First { get; set; }
    public ScreeningAnswer? Second { get; set; }
    public List<string>? InjectionFlags { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>The screening of a multivocal run's grey sources (multivocal-screening.json), which is in the run archive.</summary>
public sealed class MultivocalScreeningFile
{
    public Guid RunId { get; set; }
    public DateTime ScreenedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";

    /// <summary>Cohen's kappa between the two screenings, over the sources both screened.</summary>
    public double? Kappa { get; set; }
    public List<GreyScreening> Sources { get; set; } = new();

    public int Count(string decision) => Sources.Count(s => s.Decision == decision);
}

/// <summary>
/// Screens the grey sources of a searched multivocal run (MLR block E, G9–G10): every source twice, independently, with
/// the plan's criteria and the same steps and answer format as the systematic review's screening
/// (<see cref="PrismaReviewEngine.ScreeningProcedure"/>), so formal and grey sources get the same care. A source the two
/// screenings disagree on is kept and flagged, as in the systematic review. Sources with the same title under another
/// address (an OpenAlex work with several DOIs, seen in a live run) are screened once. The run goes from "Searched"
/// through "Screening" to "Screened".
/// </summary>
public sealed class MultivocalScreener
{
    public const string ScreeningFile = "multivocal-screening.json";
    public const string StageScreening = "Screening";
    public const string StageScreened = "Screened";

    /// <summary>Part of the cache key: raise it whenever the grey screening prompt changes.</summary>
    public const string PromptVersion = "grey-screening-v1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalScreener");

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly Func<IChatCompletionService> _chat;
    private readonly ReviewCache _cache;
    private readonly string _model;
    private readonly int _parallelism;

    /// <summary>A screener that asks <paramref name="chat"/>'s model, several sources at a time.</summary>
    public MultivocalScreener(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, Func<IChatCompletionService> chat,
        ReviewCache? cache = null, string model = "", int parallelism = 4)
    {
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _chat = chat;
        _cache = cache ?? ReviewCache.Disabled;
        _model = model;
        _parallelism = Math.Max(1, parallelism);
    }

    public MultivocalScreeningFile? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), ScreeningFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalScreeningFile>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
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
            Model = _model,
            InclusionCriteria = plan.InclusionCriteria,
            ExclusionCriteria = plan.ExclusionCriteria,
        };

        // The same title by the same producer under another address is screened once; the copies point to it.
        var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);
        var toScreen = new List<(int Index, GreyRecord Record)>();
        foreach (var found in ledger.Sources)
        {
            var r = found.Record;
            var entry = new GreyScreening { Address = MultivocalSearcher.AddressKey(r.Url), Title = r.Title, Url = r.Url };
            string title = SameSourceKey(r);
            if (title.Length > 0 && byTitle.TryGetValue(title, out var first))
            {
                entry.Decision = "Duplicate";
                entry.DuplicateOf = first;
                entry.Reasoning = "The same title by the same producer was found under another address; that source was screened.";
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

        var pairs = new List<(bool, bool)>();
        for (int i = 0; i < work.Count; i++)
        {
            var screened = await work[i];
            var entry = file.Sources[toScreen[i].Index];
            Apply(entry, screened, toScreen[i].Record);
            if (screened.First != null && screened.Second != null) pairs.Add((screened.First.IsIncluded, screened.Second.IsIncluded));
            progress?.Report($"{i + 1} of {work.Count} sources screened");
        }
        if (pairs.Count > 0) file.Kappa = Math.Round(ScreeningConfusion.From(pairs).CohensKappa, 3);
        return file;
    }

    private sealed record Screened(ScreeningAnswer? First, ScreeningAnswer? Second, int CacheHits, IReadOnlyList<string> Flags, string? Error);

    private async Task<Screened> ScreenOneAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord record)
    {
        var flags = PromptSafety.Scan(record.Title, record.Summary, record.Producer);
        try
        {
            var (first, hit1) = await ScreenCachedAsync(chat, plan, record, secondScreener: false);
            var (second, hit2) = await ScreenCachedAsync(chat, plan, record, secondScreener: true);
            return new Screened(first, second, (hit1 ? 1 : 0) + (hit2 ? 1 : 0), flags, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One failed source does not stop the screening; it is logged as not screened.
            string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.LogWarning("Grey screening failed for '{Title}': {Message}", record.Title.Replace('\r', ' ').Replace('\n', ' '), message);
            return new Screened(null, null, 0, flags, message);
        }
    }

    private async Task<(ScreeningAnswer Answer, bool FromCache)> ScreenCachedAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord r, bool secondScreener)
    {
        string key = ReviewCache.Key(PromptVersion, secondScreener ? "second" : "first", _model, plan.InclusionCriteria, plan.ExclusionCriteria,
            r.Title, r.Summary, r.Site, r.Kind, r.Producer, r.Published?.ToString("yyyy-MM-dd"));
        if (_cache.TryGet<ScreeningAnswer>("grey-screening", key, out var cached) && cached != null && ScreeningAnswer.Validate(cached) == null)
            return (cached.Normalized(), true);
        var answer = await ScreenGreyAsync(chat, r, plan.InclusionCriteria, plan.ExclusionCriteria, secondScreener);
        _cache.Set("grey-screening", key, answer);
        return (answer, false);
    }

    /// <summary>
    /// The screening prompt for one grey source: the systematic review's steps and answer format, with the record
    /// described as what it is. It is judged on its subject against the criteria, not on being grey literature.
    /// </summary>
    public static async Task<ScreeningAnswer> ScreenGreyAsync(IChatCompletionService chat, GreyRecord r, string inclusion, string exclusion, bool secondScreener)
    {
        string kind = MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == r.Kind).Label ?? r.Kind;
        string record = $"""
            - Title: {r.Title}
            - Kind of source: {kind}
            - Published on: {r.Site}
            - By: {(r.Producer.Length > 0 ? r.Producer : "not given")}
            - Date: {(r.Published is DateTime d ? d.ToString("yyyy-MM-dd") : "not given")}
            - Abstract or summary: {(r.Summary.Length > 0 ? r.Summary : "none")}
            """;

        var prompt = $$"""
            Evaluate the following grey literature source (a blog post, Q&A thread, code repository, report, thesis or similar) against the reviewer's selection criteria for a multivocal literature review.
            Judge it by its subject against the criteria, exactly as an academic paper would be judged. Being grey literature, informal or not peer-reviewed is not a reason to exclude it; its quality is assessed in a later step.

            CRITERIA DIRECTIVES (from the reviewer):
            - Inclusion Thresholds: {{inclusion}}
            - Exclusion Thresholds: {{(exclusion.Length > 0 ? exclusion : "none")}}

            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            SOURCE TARGET DATA:
            {{PromptSafety.Wrap(record, "grey source metadata and summary")}}

            {{PrismaReviewEngine.ScreeningProcedure(secondScreener)}}

            {{PrismaReviewEngine.ScreeningAnswerFormat}}
            """;

        var answer = await LlmJson.GetAsync<ScreeningAnswer>(chat, prompt, LlmJson.JsonMode(0.0), ScreeningAnswer.Validate);
        return answer.Normalized();
    }

    /// <summary>The same rules as the systematic review: a disagreement keeps the source and flags it.</summary>
    private static void Apply(GreyScreening entry, Screened s, GreyRecord record)
    {
        entry.InjectionFlags = s.Flags.Count > 0 ? s.Flags.ToList() : null;
        if (s.Error != null || s.First == null || s.Second == null)
        {
            entry.Decision = "Error";
            entry.Reasoning = $"Not screened: the model call failed ({s.Error}).";
            entry.Uncertain = true;
            return;
        }
        var (first, second) = (s.First, s.Second);
        bool agree = first.IsIncluded == second.IsIncluded;
        entry.First = first;
        entry.Second = second;
        entry.FromCache = s.CacheHits == 2;
        entry.Decision = agree ? first.Decision : "Included";
        entry.Reasoning = agree
            ? first.Reasoning
            : $"The two independent screenings disagreed (first: {first.Decision}, second: {second.Decision}), so the source was kept and flagged for the reviewer. First screening: {first.Reasoning}";
        entry.Summary = first.BriefSummary;
        entry.Confidence = new[] { first.Confidence, second.Confidence }.Contains("low") ? "low"
            : new[] { first.Confidence, second.Confidence }.Contains("medium") ? "medium"
            : first.Confidence ?? second.Confidence;
        if (record.Summary.Length == 0)
        {
            entry.Confidence = "low";
            entry.Reasoning = "(No summary was available, so this decision rests on the title and site only.) " + entry.Reasoning;
        }
        entry.Uncertain = !agree || entry.Confidence == "low" || entry.InjectionFlags != null;
        entry.ExclusionReason = entry.Decision == "Excluded" ? first.ExclusionReason ?? second.ExclusionReason ?? ExclusionReasons.Unspecified : null;
    }

    /// <summary>
    /// What spots the same source under two addresses: its title and producer, in lower case with letters and digits
    /// only. Both are needed, since two different blogs can share a title such as "Context engineering". Empty when
    /// the source has no title.
    /// </summary>
    public static string SameSourceKey(GreyRecord r)
    {
        string title = Plain(r.Title);
        return title.Length == 0 ? "" : $"{title}|{Plain(r.Producer)}";
    }

    private static string Plain(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
}

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

/// <summary>The answer to one checklist item for one source.</summary>
public sealed class QualityAnswer
{
    public string Id { get; set; } = "";

    /// <summary>1, 0.5 or 0.</summary>
    public double Score { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>The model's quote from the page, when it gave one.</summary>
    public string? Quote { get; set; }

    /// <summary>"code" (decided in code), "model" (the model's answer, its quote found in the page) or "lowered" (the model's quote was not in the page, so its points were not given).</summary>
    public string Basis { get; set; } = "";
}

/// <summary>The quality assessment of one included grey source.</summary>
public sealed class GreyQuality
{
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>The SHA-256 of the page text the answers were checked against.</summary>
    public string TextSha256 { get; set; } = "";
    public List<QualityAnswer> Answers { get; set; } = new();
    public double Points { get; set; }

    /// <summary>"Passed" (at or above the threshold), "Below threshold" or "Not assessed" (no readable page).</summary>
    public string Outcome { get; set; } = "";
    public string? NotAssessed { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>The quality assessment of a multivocal run (multivocal-quality.json), which is in the run archive.</summary>
public sealed class MultivocalQualityFile
{
    public Guid RunId { get; set; }
    public DateTime AssessedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public int Threshold { get; set; }
    public int MaxPoints { get; set; } = GreyQualityChecklist.MaxPoints;
    public string ImpactRule { get; set; } = GreyQualityChecklist.ImpactRule;
    public List<GreyQuality> Sources { get; set; } = new();

    public int Count(string outcome) => Sources.Count(s => s.Outcome == outcome);
}

/// <summary>
/// Scores the included grey sources of a screened multivocal run on the quality checklist (MLR block E, G11, Table 7).
/// For each source the page text is kept first (<see cref="MultivocalPages"/>); the model answers the 17 items that
/// need reading, each point backed by a quote, and a quote that is not in the kept text gives no points. The date,
/// the impact and the outlet type are decided in code (<see cref="GreyQualityChecklist"/>). A source at or above the
/// plan's threshold passes; one below it is excluded for quality with its score; one whose page cannot be read is
/// not assessed and says why. The run goes from "Screened" through "Assessing" to "Assessed".
/// </summary>
public sealed class MultivocalQualityAssessor
{
    public const string QualityFile = "multivocal-quality.json";
    public const string StageAssessing = "Assessing";
    public const string StageAssessed = "Assessed";

    /// <summary>Part of the cache key: raise it whenever the quality prompt changes.</summary>
    public const string PromptVersion = "grey-quality-v1";

    /// <summary>At most this much of a page is sent to the model; a quote must come from this part.</summary>
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

        string forModel = text.Length <= MaxTextForModel ? text : text[..MaxTextForModel];
        try
        {
            var (answers, fromCache) = await AnswerCachedAsync(chat, plan, record, forModel, snapshot.TextSha256);
            result.FromCache = fromCache;
            result.Answers = Score(record, answers, forModel);
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

    /// <summary>
    /// The 20 answers: the three decided in code, and the model's 17 with their quotes checked. A point the model gives
    /// without a quote found in the text is not given (its reason says so); for 3.3, asked the other way round, a
    /// lowered score needs the quote and an unsupported one is raised back to 1.
    /// </summary>
    public static List<QualityAnswer> Score(GreyRecord record, IReadOnlyList<ModelQualityAnswer> model, string text)
    {
        var byId = model.GroupBy(a => a.Id.Trim()).ToDictionary(g => g.Key, g => g.First());
        var answers = new List<QualityAnswer>();
        foreach (var item in GreyQualityChecklist.Items)
        {
            if (item.ByCode)
            {
                var (score, why) = item.Id switch
                {
                    "4.1" => GreyQualityChecklist.Date(record.Published),
                    "7.1" => GreyQualityChecklist.Impact(record.Signals),
                    _ => GreyQualityChecklist.OutletTier(record.Kind),
                };
                answers.Add(new QualityAnswer { Id = item.Id, Score = score, Reason = why, Basis = "code" });
                continue;
            }

            byId.TryGetValue(item.Id, out var a);
            double given = a == null ? 0 : Snap(a.Score);
            string reason = a?.Reason.Trim() ?? "The model gave no answer for this item.";
            string? quote = string.IsNullOrWhiteSpace(a?.Quote) ? null : a!.Quote!.Trim();
            bool found = quote != null && CitationSupportChecker.QuoteOccursIn(quote, text);
            bool needsQuote = item.QuoteForLow ? given < 1 : given > 0;
            var answer = new QualityAnswer { Id = item.Id, Score = given, Reason = reason, Quote = quote, Basis = "model" };
            if (needsQuote && !found)
            {
                answer.Score = item.QuoteForLow ? 1 : 0;
                answer.Basis = "lowered";
                answer.Reason = (item.QuoteForLow
                    ? "Scored 1: the model's evidence of a vested interest was not found in the page. "
                    : "Scored 0: the model's quote was not found in the page, so its points were not given. ") + reason;
            }
            answers.Add(answer);
        }
        return answers;
    }

    /// <summary>As in the paper's example, a source at or above the threshold passes.</summary>
    public static string OutcomeFor(double points, int threshold) => points >= threshold ? "Passed" : "Below threshold";

    private static double Snap(double score) => score >= 0.75 ? 1 : score >= 0.25 ? 0.5 : 0;

    private async Task<(IReadOnlyList<ModelQualityAnswer> Answers, bool FromCache)> AnswerCachedAsync(
        IChatCompletionService chat, MultivocalPlan plan, GreyRecord record, string text, string textSha)
    {
        string topic = TopicLine(plan);
        string key = ReviewCache.Key(PromptVersion, _model, topic, record.Title, record.Producer, record.Site, record.Kind, textSha);
        if (_cache.TryGet<ModelQualityAnswers>("grey-quality", key, out var cached) && cached != null && ModelQualityAnswers.Validate(cached) == null)
            return (cached.Answers, true);
        var answer = await AskAsync(chat, topic, record, text);
        _cache.Set("grey-quality", key, answer);
        return (answer.Answers, false);
    }

    private static string TopicLine(MultivocalPlan plan) =>
        $"{plan.Topic}. Research questions: {string.Join(" ", plan.Numbered().Select(q => $"{q.Number}: {q.Question.Text}"))}";

    /// <summary>The quality prompt: the 17 items read from the page, each scored 1, 0.5 or 0 with a verbatim quote.</summary>
    public static async Task<ModelQualityAnswers> AskAsync(IChatCompletionService chat, string topic, GreyRecord record, string text)
    {
        string items = string.Join("\n", GreyQualityChecklist.ModelItems.Select(i => $"{i.Id} ({i.Group}) {i.Question}"));
        string about = $"""
            - Title: {record.Title}
            - Address: {record.Url}
            - Published on: {record.Site}
            - By: {(record.Producer.Length > 0 ? record.Producer : "not given")}
            """;
        var prompt = $$"""
            You assess the quality of one grey literature source for a multivocal literature review, with the quality checklist of Garousi et al. (2019, Table 7).
            The review is about: {{PromptSafety.Wrap(topic, "review topic")}}

            {{PromptSafety.DataOnlyNotice}}

            SOURCE:
            {{PromptSafety.Wrap(about, "source metadata")}}

            PAGE TEXT (the only evidence you may use):
            {{PromptSafety.Wrap(text, "page text")}}

            Answer each item below about this source. Score 1 (yes), 0.5 (partly) or 0 (no, or the page does not show it).
            For every score above 0, copy a short passage (one or two sentences) word for word from the page text that shows it; without one the score is 0.
            Item 3.3 is asked the other way round: score 1 when there is no sign of a vested interest, and give a quote only when you score it lower.
            Judge only from the page text, not from what you may know about the author or the site.

            ITEMS:
            {{items}}

            Respond ONLY with a valid minified JSON object of this shape, with one entry per item, in order:
            {"answers":[{"id":"1.1","score":1,"reason":"One sentence.","quote":"Word for word from the page."}]}
            """;
        return await LlmJson.GetAsync<ModelQualityAnswers>(chat, prompt, LlmJson.JsonMode(0.0), ModelQualityAnswers.Validate);
    }
}

/// <summary>The model's answer to one checklist item.</summary>
public sealed class ModelQualityAnswer
{
    public string Id { get; set; } = "";
    public double Score { get; set; }
    public string Reason { get; set; } = "";
    public string? Quote { get; set; }
}

/// <summary>The model's answers to the checklist, as JSON.</summary>
public sealed class ModelQualityAnswers
{
    public List<ModelQualityAnswer> Answers { get; set; } = new();

    /// <summary>Null when the answer is usable: every model item answered once, with a score of 1, 0.5 or 0 and a reason.</summary>
    public static string? Validate(ModelQualityAnswers a)
    {
        var expected = GreyQualityChecklist.ModelItems.Select(i => i.Id).ToList();
        var ids = a.Answers.Select(x => x.Id.Trim()).ToList();
        var missing = expected.Except(ids).ToList();
        if (missing.Count > 0) return $"answers are missing for items {string.Join(", ", missing)}.";
        if (a.Answers.Any(x => x.Score is not (0 or 0.5 or 1))) return "every score must be 1, 0.5 or 0.";
        if (a.Answers.Any(x => string.IsNullOrWhiteSpace(x.Reason))) return "every answer needs a reason.";
        return null;
    }
}

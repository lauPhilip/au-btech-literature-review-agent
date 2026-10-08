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

    /// <summary>For an opinion piece: its main claim and the critical questions for an expert opinion (G13); null otherwise.</summary>
    public GreyArgument? Argument { get; set; }
}

/// <summary>One critical question about an opinion piece, and its answer.</summary>
public sealed class ArgumentAnswer
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string? Quote { get; set; }

    /// <summary>"model" (its quote found in the page), "unsupported" (the quote was not found), "reviewer" or "synthesis".</summary>
    public string Basis { get; set; } = "";
}

/// <summary>
/// The argument of an opinion piece (G13): the source's main claim P, as written, and the six critical questions for
/// an expert opinion that Garousi et al. take from Rainer's work. The model answers expertise, field, opinion and
/// backup evidence from the page; trustworthiness is left to the reviewer, as the paper notes it cannot be judged
/// reliably, and consistency with other experts is judged in the synthesis, where the sources are compared.
/// </summary>
public sealed class GreyArgument
{
    public string Claim { get; set; } = "";
    public bool ClaimFound { get; set; }
    public List<ArgumentAnswer> Questions { get; set; } = new();

    /// <summary>The six questions, in the paper's order; P is the claim and W its writer.</summary>
    public static readonly IReadOnlyList<(string Id, string Question)> CriticalQuestions = new[]
    {
        ("expertise", "How credible is the writer as an expert source?"),
        ("field", "Is the writer an expert in the field the claim belongs to?"),
        ("opinion", "What did the writer assert that implies the claim?"),
        ("trustworthiness", "Is the writer personally reliable as a source?"),
        ("consistency", "Does the claim agree with what other experts say?"),
        ("backup", "Is the writer's assertion based on evidence?"),
    };
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
    public const string PromptVersion = "grey-quality-v2";

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

    /// <summary>
    /// The argument record of an opinion piece, with every quote checked against the page: a claim or answer whose
    /// quote is not in the page is kept but marked as unsupported. Null when the source is not an opinion piece.
    /// </summary>
    public static GreyArgument? Argument(ModelOpinion? opinion, string text)
    {
        if (opinion is not { IsOpinion: true }) return null;
        var argument = new GreyArgument
        {
            Claim = opinion.Claim?.Trim() ?? "",
            ClaimFound = CitationSupportChecker.QuoteOccursIn(opinion.Claim, text),
        };
        foreach (var (id, question) in GreyArgument.CriticalQuestions)
        {
            var answer = new ArgumentAnswer { Id = id, Question = question };
            if (id == "trustworthiness")
            {
                answer.Answer = "Left to the reviewer: Garousi et al. note that this cannot be judged reliably.";
                answer.Basis = "reviewer";
            }
            else if (id == "consistency")
            {
                answer.Answer = "Judged in the synthesis, where the claim is compared with the other sources; experts may also disagree for good reasons.";
                answer.Basis = "synthesis";
            }
            else
            {
                var given = id switch { "expertise" => opinion.Expertise, "field" => opinion.Field, "opinion" => opinion.Assertion, _ => opinion.Evidence };
                answer.Answer = given?.Answer?.Trim() ?? "No answer.";
                answer.Quote = string.IsNullOrWhiteSpace(given?.Quote) ? null : given!.Quote!.Trim();
                bool found = answer.Quote != null && CitationSupportChecker.QuoteOccursIn(answer.Quote, text);
                answer.Basis = found ? "model" : "unsupported";
                if (!found) answer.Answer = "(No quote from the page backs this.) " + answer.Answer;
            }
            argument.Questions.Add(answer);
        }
        return argument;
    }

    private async Task<(ModelQualityAnswers Answers, bool FromCache)> AnswerCachedAsync(
        IChatCompletionService chat, MultivocalPlan plan, GreyRecord record, string text, string textSha)
    {
        string topic = TopicLine(plan);
        string key = ReviewCache.Key(PromptVersion, _model, topic, record.Title, record.Producer, record.Site, record.Kind, textSha);
        if (_cache.TryGet<ModelQualityAnswers>("grey-quality", key, out var cached) && cached != null && ModelQualityAnswers.Validate(cached) == null)
            return (cached, true);
        var answer = await AskAsync(chat, topic, record, text);
        _cache.Set("grey-quality", key, answer);
        return (answer, false);
    }

    private static string TopicLine(MultivocalPlan plan) =>
        $"{plan.Topic}. Research questions: {string.Join(" ", plan.Numbered().Select(q => $"{q.Number}: {q.Question.Text}"))}";

    private const string AnswerShape = """
        {"answers":[{"id":"1.1","score":1,"reason":"One sentence.","quote":"Word for word from the page."}],"opinion":{"isOpinion":true,"claim":"Word for word from the page.","expertise":{"answer":"One sentence.","quote":"Word for word."},"field":{"answer":"...","quote":"..."},"assertion":{"answer":"...","quote":"..."},"evidence":{"answer":"...","quote":"..."}}}
        """;

    /// <summary>
    /// The quality prompt: the 17 items read from the page, each scored 1, 0.5 or 0 with a verbatim quote, and for an
    /// opinion piece its main claim and four of the critical questions for an expert opinion (G13).
    /// </summary>
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

            Then decide whether the source is an OPINION PIECE: it mainly argues for a view from the writer's own experience or judgement, rather than reporting data, a study or documentation.
            If it is, copy its main claim word for word from the page, and answer these questions about the claim and its writer, each in one sentence with a word-for-word quote from the page:
            - expertise: How credible is the writer as an expert source?
            - field: Is the writer an expert in the field the claim belongs to?
            - assertion: What did the writer assert that implies the claim?
            - evidence: Is the writer's assertion based on evidence (data, examples, measurements, references)?
            If it is not an opinion piece, set isOpinion to false and leave the rest out.

            Respond ONLY with a valid minified JSON object of this shape, with one entry per item, in order:
            {{AnswerShape}}
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

    /// <summary>For an opinion piece, its claim and the answers to four critical questions; older answers have none.</summary>
    public ModelOpinion? Opinion { get; set; }

    /// <summary>Null when the answer is usable: every model item answered once, with a score of 1, 0.5 or 0 and a reason.</summary>
    public static string? Validate(ModelQualityAnswers a)
    {
        var expected = GreyQualityChecklist.ModelItems.Select(i => i.Id).ToList();
        var ids = a.Answers.Select(x => x.Id.Trim()).ToList();
        var missing = expected.Except(ids).ToList();
        if (missing.Count > 0) return $"answers are missing for items {string.Join(", ", missing)}.";
        if (a.Answers.Any(x => x.Score is not (0 or 0.5 or 1))) return "every score must be 1, 0.5 or 0.";
        if (a.Answers.Any(x => string.IsNullOrWhiteSpace(x.Reason))) return "every answer needs a reason.";
        if (a.Opinion is { IsOpinion: true } o)
        {
            if (string.IsNullOrWhiteSpace(o.Claim)) return "an opinion piece needs its main claim, word for word.";
            if (new[] { o.Expertise, o.Field, o.Assertion, o.Evidence }.Any(x => string.IsNullOrWhiteSpace(x?.Answer)))
                return "an opinion piece needs answers to expertise, field, assertion and evidence.";
        }
        return null;
    }
}

/// <summary>The model's reading of an opinion piece: its claim and the answers to four critical questions.</summary>
public sealed class ModelOpinion
{
    public bool IsOpinion { get; set; }
    public string? Claim { get; set; }
    public ModelOpinionAnswer? Expertise { get; set; }
    public ModelOpinionAnswer? Field { get; set; }
    public ModelOpinionAnswer? Assertion { get; set; }
    public ModelOpinionAnswer? Evidence { get; set; }
}

/// <summary>One answer about an opinion piece, with its quote from the page.</summary>
public sealed class ModelOpinionAnswer
{
    public string? Answer { get; set; }
    public string? Quote { get; set; }
}

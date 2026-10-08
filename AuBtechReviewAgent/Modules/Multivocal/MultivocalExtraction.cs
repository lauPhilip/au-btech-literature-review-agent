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

/// <summary>One value of one attribute for one source, with the quote from the page that shows it.</summary>
public sealed class ExtractedValue
{
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
}

/// <summary>What one source says for one attribute of the map.</summary>
public sealed class ExtractedAttribute
{
    public string Attribute { get; set; } = "";

    /// <summary>The values whose quote was found in the page.</summary>
    public List<ExtractedValue> Values { get; set; } = new();

    /// <summary>The values the model gave whose quote was not found in the page; kept on file, never used.</summary>
    public List<ExtractedValue> Unsupported { get; set; } = new();

    /// <summary>True when the source states nothing for this attribute (no supported value).</summary>
    public bool NotStated => Values.Count == 0;
}

/// <summary>The extraction of one source against the fixed map.</summary>
public sealed class GreyExtraction
{
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string TextSha256 { get; set; } = "";

    /// <summary>The facts the run already knows, recorded by code (<see cref="MultivocalMapper.KnownFacts"/>).</summary>
    public List<KnownFact> Known { get; set; } = new();

    /// <summary>What the source sets out to do (G12), with its quote; null when the model's quote was not in the page.</summary>
    public ExtractedValue? Purpose { get; set; }

    /// <summary>The research questions the source informs: those with at least one supported value, decided in code.</summary>
    public List<string> Coverage { get; set; } = new();
    public List<ExtractedAttribute> Attributes { get; set; } = new();
    public string? Error { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>A fact about a source that code records.</summary>
public sealed class KnownFact
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>The extraction of a multivocal run (multivocal-extraction.json), which is in the run archive.</summary>
public sealed class MultivocalExtractionFile
{
    public Guid RunId { get; set; }
    public DateTime ExtractedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>The map version every source was extracted against.</summary>
    public int MapVersion { get; set; }
    public List<GreyExtraction> Sources { get; set; } = new();

    /// <summary>How many sources have each value of an attribute: the systematic map's counts (G12), for RQs about frequency.</summary>
    public IReadOnlyList<(string Value, int Sources)> Counts(string attribute)
    {
        var extracted = Sources.Where(s => s.Error == null).Select(s => s.Attributes.FirstOrDefault(a => a.Attribute == attribute)).ToList();
        var counts = extracted.Where(a => a != null).SelectMany(a => a!.Values.Select(v => v.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Value: g.Key, Sources: g.Count()))
            .OrderByDescending(x => x.Sources).ThenBy(x => x.Value)
            .ToList();
        int notStated = extracted.Count(a => a == null || a.NotStated);
        if (notStated > 0) counts.Add(("not stated", notStated));
        return counts;
    }
}

/// <summary>
/// Extracts every source that passed the quality check against the fixed systematic map (MLR block F, G12): for each
/// attribute the values the page states, each with a quote that must be found in the kept page text, or "not stated".
/// A value of a closed attribute must be one of its values, and a single-choice attribute takes one value. The known
/// facts and the coverage are recorded by code. The run goes from "Mapped" through "Extracting" to "Extracted".
/// </summary>
public sealed class MultivocalExtractor
{
    public const string ExtractionFile = "multivocal-extraction.json";
    public const string StageExtracting = "Extracting";
    public const string StageExtracted = "Extracted";
    public const string PromptVersion = "grey-extraction-v1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalExtractor");

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly MultivocalQualityAssessor _quality;
    private readonly MultivocalMapper _mapper;
    private readonly MultivocalPages _pages;
    private readonly Func<IChatCompletionService> _chat;
    private readonly ReviewCache _cache;
    private readonly string _model;
    private readonly int _parallelism;

    /// <summary>An extractor that reads the kept pages and asks <paramref name="chat"/>'s model, several sources at a time.</summary>
    public MultivocalExtractor(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, MultivocalQualityAssessor quality,
        MultivocalMapper mapper, MultivocalPages pages, Func<IChatCompletionService> chat, ReviewCache? cache = null, string model = "", int parallelism = 4)
    {
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _quality = quality;
        _mapper = mapper;
        _pages = pages;
        _chat = chat;
        _cache = cache ?? ReviewCache.Disabled;
        _model = model;
        _parallelism = Math.Max(1, parallelism);
    }

    public MultivocalExtractionFile? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), ExtractionFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalExtractionFile>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>Extracts every source that passed the quality check. Needs the run's edit key and a fixed map.</summary>
    public async Task<MultivocalExtractionFile> ExtractAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can extract its sources.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        var map = _mapper.Load(runId);
        var fixedMap = map?.FixedVersion is int number ? map.Versions.First(v => v.Number == number) : throw new InvalidOperationException("Fix the map first.");
        var quality = _quality.Load(runId) ?? throw new InvalidOperationException("Score the quality first.");
        var records = (_searcher.LoadLedger(runId)?.Sources ?? new()).ToDictionary(s => MultivocalSearcher.AddressKey(s.Record.Url), s => s.Record);
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run is being extracted already.");

        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage is not (MultivocalMapper.StageMapped or StageExtracting)) throw new InvalidOperationException("This run has been extracted already.");
            await RunHeader.WriteAsync(folder, header with { Stage = StageExtracting });

            var passed = quality.Sources.Where(q => q.Outcome == "Passed" && records.ContainsKey(q.Address)).ToList();
            var snapshots = _pages.Load(runId).Pages.ToDictionary(p => MultivocalSearcher.AddressKey(p.Url));
            var chat = _chat();
            using var throttle = new SemaphoreSlim(_parallelism);
            var work = passed.Select(async q =>
            {
                await throttle.WaitAsync();
                try { return await ExtractOneAsync(runId, chat, planned.Plan, fixedMap, records[q.Address], q, snapshots.GetValueOrDefault(q.Address)); }
                finally { throttle.Release(); }
            }).ToList();

            var file = new MultivocalExtractionFile { RunId = runId, ExtractedUtc = DateTime.UtcNow, PromptVersion = PromptVersion, Model = _model, MapVersion = fixedMap.Number };
            for (int i = 0; i < work.Count; i++)
            {
                file.Sources.Add(await work[i]);
                progress?.Report($"{i + 1} of {work.Count} sources extracted");
            }

            await SafeFile.WriteAllTextAsync(Path.Join(folder, ExtractionFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageExtracted });
            return file;
        }
        catch
        {
            if (_runs.LoadHeader(runId) is { Stage: StageExtracting } current)
                await RunHeader.WriteAsync(folder, current with { Stage = MultivocalMapper.StageMapped });
            throw;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    private async Task<GreyExtraction> ExtractOneAsync(Guid runId, IChatCompletionService chat, MultivocalPlan plan, MapVersion map,
        GreyRecord record, GreyQuality quality, PageSnapshot? snapshot)
    {
        var result = new GreyExtraction
        {
            Address = quality.Address,
            Title = record.Title,
            Url = record.Url,
            Known = MultivocalMapper.KnownFactsFor(record, quality).Select(f => new KnownFact { Name = f.Name, Value = f.Value }).ToList(),
        };
        string? text = snapshot is { NotKept: null } ? _pages.ReadText(runId, snapshot) : null;
        if (text == null)
        {
            result.Error = "The kept page text is missing or no longer matches its fingerprint.";
            return result;
        }
        result.TextSha256 = snapshot!.TextSha256;
        string forModel = text.Length <= MultivocalQualityAssessor.MaxTextForModel ? text : text[..MultivocalQualityAssessor.MaxTextForModel];

        try
        {
            string key = ReviewCache.Key(PromptVersion, _model, JsonSerializer.Serialize(map.Attributes), plan.Topic, record.Title, snapshot.TextSha256);
            ModelExtraction answer;
            if (_cache.TryGet<ModelExtraction>("grey-extraction", key, out var cached) && cached != null && ModelExtraction.Problem(cached, map) == null)
            {
                answer = cached;
                result.FromCache = true;
            }
            else
            {
                answer = await AskAsync(chat, plan, map, record, forModel);
                _cache.Set("grey-extraction", key, answer);
            }
            Verify(result, answer, map, plan, forModel);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.LogWarning("Extraction failed for '{Title}': {Message}", record.Title.Replace('\r', ' ').Replace('\n', ' '), message);
            result.Error = $"The model call failed ({message}).";
        }
        return result;
    }

    /// <summary>
    /// Keeps only what the page shows: a value whose quote is not found in the text is moved to Unsupported. The
    /// coverage is every research question with at least one supported value.
    /// </summary>
    public static void Verify(GreyExtraction result, ModelExtraction answer, MapVersion map, MultivocalPlan plan, string text)
    {
        if (answer.Purpose is { } purpose && !string.IsNullOrWhiteSpace(purpose.Value) && CitationSupportChecker.QuoteOccursIn(purpose.Quote, text))
            result.Purpose = new ExtractedValue { Value = purpose.Value.Trim(), Quote = purpose.Quote.Trim() };

        result.Attributes = map.Attributes.Select(attribute =>
        {
            var given = answer.Attributes.FirstOrDefault(a => a.Attribute.Trim().Equals(attribute.Name, StringComparison.OrdinalIgnoreCase));
            var extracted = new ExtractedAttribute { Attribute = attribute.Name };
            foreach (var v in given?.Values ?? new())
            {
                var value = new ExtractedValue { Value = Canonical(attribute, v.Value), Quote = (v.Quote ?? "").Trim() };
                if (CitationSupportChecker.QuoteOccursIn(value.Quote, text)) extracted.Values.Add(value);
                else extracted.Unsupported.Add(value);
            }
            return extracted;
        }).ToList();

        var questions = plan.Numbered().Select(q => q.Number).ToList();
        result.Coverage = questions
            .Where(q => map.Attributes.Any(a => a.Question == q && result.Attributes.Any(x => x.Attribute == a.Name && !x.NotStated)))
            .ToList();
    }

    /// <summary>A closed attribute's value as the map writes it ("retrieval" becomes "Retrieval").</summary>
    private static string Canonical(MapAttribute attribute, string value) =>
        attribute.Open ? value.Trim() : attribute.Values.FirstOrDefault(v => v.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    /// <summary>The extraction prompt: every attribute of the fixed map, each value with a word-for-word quote.</summary>
    public static async Task<ModelExtraction> AskAsync(IChatCompletionService chat, MultivocalPlan plan, MapVersion map, GreyRecord record, string text)
    {
        string attributes = string.Join("\n", map.Attributes.Select(a =>
            $"- {a.Name} ({a.Question}; {(a.Open ? "open text" : a.Multiple ? "one or more of: " + string.Join(" | ", a.Values) : "exactly one of: " + string.Join(" | ", a.Values))}): {a.Description}"));
        string about = $"""
            - Title: {record.Title}
            - Address: {record.Url}
            - Published on: {record.Site}
            """;
        var prompt = $$"""
            You extract data from one grey literature source for a multivocal literature review (Garousi et al. 2019, G12), against the review's systematic map.
            The review is about: {{PromptSafety.Wrap(plan.Topic, "review topic")}}

            {{PromptSafety.DataOnlyNotice}}

            SOURCE:
            {{PromptSafety.Wrap(about, "source metadata")}}

            PAGE TEXT (the only evidence you may use):
            {{PromptSafety.Wrap(text, "page text")}}

            ATTRIBUTES OF THE MAP (from the reviewer):
            {{PromptSafety.Wrap(attributes, "map attributes")}}

            For every attribute, give the values this page states, each with a short passage copied word for word from the page text that shows it.
            Use only the listed values for an attribute with a list; use "Other" only when it is listed and nothing else fits. Give at most one value where it says "exactly one".
            When the page says nothing for an attribute, give no values for it: "not stated" is recorded automatically. Never guess.
            Also say in one sentence what the source sets out to do (its purpose), with a word-for-word quote.

            {{AnswerShape}}
            """;
        return await LlmJson.GetAsync<ModelExtraction>(chat, prompt, LlmJson.JsonMode(0.0), a => ModelExtraction.Problem(a, map));
    }

    private const string AnswerShape = """
        Respond ONLY with a valid minified JSON object of this shape, with one entry per attribute:
        {"purpose":{"value":"One sentence.","quote":"Word for word from the page."},"attributes":[{"attribute":"Practice","values":[{"value":"Retrieval","quote":"Word for word from the page."}]}]}
        """;
}

/// <summary>The model's extraction of one source.</summary>
public sealed class ModelExtraction
{
    public ExtractedValue? Purpose { get; set; }
    public List<ModelExtractedAttribute> Attributes { get; set; } = new();

    /// <summary>Null when the answer fits the map: known attributes only, listed values only, one value where only one is allowed.</summary>
    public static string? Problem(ModelExtraction a, MapVersion map)
    {
        foreach (var given in a.Attributes)
        {
            var attribute = map.Attributes.FirstOrDefault(m => m.Name.Equals(given.Attribute?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (attribute == null) return $"\"{given.Attribute}\" is not an attribute of the map.";
            if (!attribute.Multiple && given.Values.Count > 1) return $"\"{attribute.Name}\" takes exactly one value.";
            if (given.Values.Any(v => string.IsNullOrWhiteSpace(v.Value))) return $"every value of \"{attribute.Name}\" needs text.";
            if (!attribute.Open)
            {
                var unknown = given.Values.FirstOrDefault(v => !attribute.Values.Contains(v.Value.Trim(), StringComparer.OrdinalIgnoreCase));
                if (unknown != null) return $"\"{unknown.Value}\" is not one of the values of \"{attribute.Name}\" ({string.Join(", ", attribute.Values)}).";
            }
        }
        return null;
    }
}

/// <summary>The model's values for one attribute.</summary>
public sealed class ModelExtractedAttribute
{
    public string Attribute { get; set; } = "";
    public List<ExtractedValue> Values { get; set; } = new();
}

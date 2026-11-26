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
/// Extracts every source that passed the quality check against the fixed systematic map (MLR block F, G12): for each
/// attribute the values the page states, each with a quote that must be found in the kept page text, or "not stated".
/// A value of a closed attribute must be one of its values, and a single-choice attribute takes one value. The known
/// facts and the coverage are recorded by code. The run goes from "Mapped" through "Extracting" to "Extracted".
/// </summary>
public sealed partial class MultivocalExtractor
{
    public const string ExtractionFile = "multivocal-extraction.json";
    public const string StageExtracting = "Extracting";
    public const string StageExtracted = "Extracted";
    public const string PromptVersion = "grey-extraction-v4";

    /// <summary>Part of the cache key of a formal source's extraction, whose prompt names it as academic (block I-5).</summary>
    public const string FormalPromptVersion = "formal-extraction-v4";

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

            OpenNameMerger.Apply(file, fixedMap, planned.Plan);
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
            Formal = record.Kind == MultivocalSearcher.FormalKind,
        };
        string? text = snapshot is { NotKept: null } ? _pages.ReadText(runId, snapshot) : null;
        if (text == null)
        {
            result.Error = "The kept page text is missing or no longer matches its fingerprint.";
            return result;
        }
        result.TextSha256 = snapshot!.TextSha256;
        if (result.Formal) result.TextBasis = snapshot.TextBasis;
        // A long full text or page is sent as selected passages; every quote is checked against what was sent.
        var sent = result.Formal ? FormalExtractionText.Select(text, snapshot.TextBasis, plan, map) : GreyPageText.ForExtraction(text, plan, map);
        result.Passages = sent.Passages;
        string forModel = sent.Text;

        try
        {
            // A grey page's key also holds the passages sent, which depend on the questions too.
            string attributes = JsonSerializer.Serialize(map.Attributes);
            string key = result.Formal
                ? ReviewCache.Key(FormalPromptVersion, TemplateSha256(formal: true), _model, attributes, plan.Topic, record.Title, snapshot.TextSha256)
                : ReviewCache.Key(PromptVersion, TemplateSha256(formal: false), _model, attributes, plan.Topic, record.Title, snapshot.TextSha256, sent.Passages == null ? null : forModel);
            ModelExtraction answer;
            if (_cache.TryGet<ModelExtraction>("grey-extraction", key, out var cached) && cached != null && ModelExtraction.Problem(cached, map) == null)
            {
                answer = cached;
                result.FromCache = true;
            }
            else
            {
                answer = await AskAsync(chat, plan, map, record, forModel, sent.Passages != null);
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
        var spans = PassageSelection.Spans(text);
        if (answer.Purpose is { } purpose && !string.IsNullOrWhiteSpace(purpose.Value) && PassageSelection.QuoteFound(purpose.Quote, spans))
            result.Purpose = new ExtractedValue { Value = purpose.Value.Trim(), Quote = purpose.Quote.Trim() };

        result.Attributes = map.Attributes.Select(attribute =>
        {
            var given = answer.Attributes.FirstOrDefault(a => a.Attribute.Trim().Equals(attribute.Name, StringComparison.OrdinalIgnoreCase));
            var extracted = new ExtractedAttribute { Attribute = attribute.Name };
            foreach (var v in given?.Values ?? new())
            {
                var value = new ExtractedValue { Value = Canonical(attribute, v.Value), Quote = (v.Quote ?? "").Trim() };
                if (PassageSelection.QuoteFound(value.Quote, spans)) extracted.Values.Add(value);
                else extracted.Unsupported.Add(value);
            }
            return extracted;
        }).ToList();

        result.Coverage = CoverageOf(result, map, plan);
    }

    /// <summary>The research questions a source informs: those served by an attribute with at least one supported value.</summary>
    public static List<string> CoverageOf(GreyExtraction source, MapVersion map, MultivocalPlan plan) => plan.Numbered().Select(q => q.Number)
        .Where(q => map.Attributes.Any(a => a.Serves(q) && source.Attributes.Any(x => x.Attribute == a.Name && !x.NotStated)))
        .ToList();

    /// <summary>A closed attribute's value as the map writes it ("retrieval" becomes "Retrieval").</summary>
    private static string Canonical(MapAttribute attribute, string value) =>
        attribute.Open ? value.Trim() : attribute.Values.FirstOrDefault(v => v.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();
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

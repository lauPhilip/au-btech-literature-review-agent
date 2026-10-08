using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// One attribute of the systematic map (G12, Table 10): what is recorded about every source for one research question,
/// with the values it may take, and whether a source can have several of them. An open attribute takes free text
/// (a tool's name, say) because its values cannot be listed in advance.
/// </summary>
public sealed class MapAttribute
{
    public string Name { get; set; } = "";

    /// <summary>The research question it serves, numbered as in the protocol ("RQ1", "RQ1.1").</summary>
    public string Question { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Values { get; set; } = new();
    public bool Multiple { get; set; }
    public bool Open { get; set; }
}

/// <summary>One version of the map: who made it and when.</summary>
public sealed class MapVersion
{
    public int Number { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>"model" (proposed from the questions and the sources) or "reviewer" (edited).</summary>
    public string By { get; set; } = "";
    public List<MapAttribute> Attributes { get; set; } = new();
}

/// <summary>
/// The systematic map of a multivocal run (multivocal-map.json), which is in the run archive: every version, in order,
/// and which one was fixed for the extraction.
/// </summary>
public sealed class MultivocalMapFile
{
    public Guid RunId { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>How many sources the model saw when it generalised the values.</summary>
    public int SourcesSeen { get; set; }
    public List<MapVersion> Versions { get; set; } = new();

    /// <summary>The version used for the extraction; null until the reviewer fixes the map.</summary>
    public int? FixedVersion { get; set; }

    public MapVersion Latest => Versions[^1];
}

/// <summary>
/// Builds the systematic map of a multivocal run (MLR block F, G12). The model proposes initial attributes from the
/// research questions and generalises their values over the sources that passed the quality check, as in keyword or
/// open coding; the reviewer then edits the map, each edit kept as a new version, and fixes it before extraction.
/// The run goes from "Assessed" through "Mapping" to "Mapped".
/// </summary>
public sealed class MultivocalMapper
{
    public const string MapFile = "multivocal-map.json";
    public const string StageMapping = "Mapping";
    public const string StageMapped = "Mapped";
    public const string PromptVersion = "grey-map-v1";

    public const int MaxAttributes = 20;
    public const int MaxValues = 15;
    public const int MaxText = 300;

    /// <summary>At most this many sources are shown to the model when it generalises the values.</summary>
    public const int MaxSourcesShown = 80;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly MultivocalQualityAssessor _quality;
    private readonly Func<IChatCompletionService> _chat;
    private readonly string _model;

    /// <summary>A mapper that asks <paramref name="chat"/>'s model for the first version of the map.</summary>
    public MultivocalMapper(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, MultivocalQualityAssessor quality,
        Func<IChatCompletionService> chat, string model = "")
    {
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _quality = quality;
        _chat = chat;
        _model = model;
    }

    public MultivocalMapFile? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), MapFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalMapFile>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>Asks the model for the first version of the map, from the questions and the sources that passed.</summary>
    public async Task<MultivocalMapFile> ProposeAsync(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can map it.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        var quality = _quality.Load(runId) ?? throw new InvalidOperationException("Score the quality first.");
        var records = (_searcher.LoadLedger(runId)?.Sources ?? new()).ToDictionary(s => MultivocalSearcher.AddressKey(s.Record.Url), s => s.Record);
        var passed = quality.Sources.Where(q => q.Outcome == "Passed" && records.ContainsKey(q.Address)).Select(q => records[q.Address]).ToList();
        if (passed.Count == 0) throw new InvalidOperationException("No source passed the quality check, so there is nothing to map.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run is being mapped already.");

        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage != MultivocalQualityAssessor.StageAssessed) throw new InvalidOperationException("This run has a map already.");

            var shown = passed.Take(MaxSourcesShown).ToList();
            var answer = await AskAsync(_chat(), planned.Plan, shown);
            var file = new MultivocalMapFile { RunId = runId, PromptVersion = PromptVersion, Model = _model, SourcesSeen = shown.Count };
            file.Versions.Add(new MapVersion { Number = 1, CreatedUtc = DateTime.UtcNow, By = "model", Attributes = Clean(answer.Attributes) });

            await SafeFile.WriteAllTextAsync(Path.Join(folder, MapFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageMapping });
            return file;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    /// <summary>Keeps the reviewer's edit as a new version. Possible until the map is fixed.</summary>
    public async Task<MultivocalMapFile> SaveAsync(Guid runId, string? editKey, IReadOnlyList<MapAttribute> attributes)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can edit its map.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        var cleaned = Clean(attributes);
        if (Problem(cleaned, planned.Plan) is { } problem) throw new ArgumentException(problem);

        await Gate.WaitAsync();
        try
        {
            if (_runs.LoadHeader(runId)?.Stage != StageMapping) throw new InvalidOperationException("The map can be edited only before it is fixed.");
            var file = Load(runId) ?? throw new InvalidOperationException("Propose the map first.");
            file.Versions.Add(new MapVersion { Number = file.Versions.Count + 1, CreatedUtc = DateTime.UtcNow, By = "reviewer", Attributes = cleaned });
            await SafeFile.WriteAllTextAsync(Path.Join(_runs.FolderOf(runId), MapFile), JsonSerializer.Serialize(file, Json));
            return file;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Fixes the latest version for the extraction; after this the map cannot change.</summary>
    public async Task<MultivocalMapFile> FixAsync(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can fix its map.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        await Gate.WaitAsync();
        try
        {
            var header = _runs.LoadHeader(runId);
            if (header?.Stage != StageMapping) throw new InvalidOperationException("The map is fixed already.");
            var file = Load(runId) ?? throw new InvalidOperationException("Propose the map first.");
            if (Problem(file.Latest.Attributes, planned.Plan) is { } problem) throw new ArgumentException(problem);
            file.FixedVersion = file.Latest.Number;
            string folder = _runs.FolderOf(runId);
            await SafeFile.WriteAllTextAsync(Path.Join(folder, MapFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageMapped });
            return file;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Trims every text, drops empty and repeated values, and keeps no values on an open attribute.</summary>
    public static List<MapAttribute> Clean(IEnumerable<MapAttribute> attributes) => attributes.Select(a => new MapAttribute
    {
        Name = ReviewInputGuard.Normalize(a.Name ?? "", singleLine: true),
        Question = (a.Question ?? "").Trim().ToUpperInvariant(),
        Description = ReviewInputGuard.Normalize(a.Description ?? "", singleLine: true),
        Open = a.Open,
        Multiple = a.Multiple,
        Values = a.Open ? new() : (a.Values ?? new())
            .Select(v => ReviewInputGuard.Normalize(v ?? "", singleLine: true))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
    }).ToList();

    /// <summary>What is wrong with a map, or null when it can be used: every attribute named once, for a question of the plan, with values unless open.</summary>
    public static string? Problem(IReadOnlyList<MapAttribute> attributes, MultivocalPlan plan)
    {
        if (attributes.Count == 0) return "The map needs at least one attribute.";
        if (attributes.Count > MaxAttributes) return $"Use at most {MaxAttributes} attributes.";
        var questions = plan.Numbered().Select(q => q.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attributes)
        {
            if (a.Name.Length == 0) return "Give every attribute a name.";
            if (!questions.Contains(a.Question)) return $"\"{a.Name}\" must belong to one of the research questions ({string.Join(", ", questions)}).";
            if (new[] { a.Name, a.Description }.Concat(a.Values).Any(t => t.Length > MaxText)) return $"Keep every text in \"{a.Name}\" under {MaxText} characters.";
            if (!a.Open && a.Values.Count < 2) return $"\"{a.Name}\" needs at least two values, or mark it as open text.";
            if (a.Values.Count > MaxValues) return $"\"{a.Name}\" can have at most {MaxValues} values.";
        }
        var repeated = attributes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return repeated != null ? $"Two attributes are called \"{repeated.Key}\"." : null;
    }

    /// <summary>The map prompt: initial attributes from the questions, values generalised over the sources.</summary>
    public static async Task<ModelMapAnswer> AskAsync(IChatCompletionService chat, MultivocalPlan plan, IReadOnlyList<GreyRecord> sources)
    {
        string questions = string.Join("\n", plan.Numbered().Select(q => $"{q.Number} ({MultivocalGuidelines.FindType(q.Question.Type)?.Name ?? "not set"}): {q.Question.Text}"));
        string list = string.Join("\n", sources.Select((s, i) =>
            $"{i + 1}. {s.Title} ({s.Site}): {(s.Summary.Length > 300 ? s.Summary[..300] + "…" : s.Summary)}"));
        var prompt = $$"""
            You build the systematic map (classification scheme) of a multivocal literature review, following Garousi et al. (2019, G12 and Table 10).
            Topic: {{PromptSafety.Wrap(plan.Topic, "review topic")}}

            RESEARCH QUESTIONS (from the reviewer):
            {{PromptSafety.Wrap(questions, "research questions")}}

            {{PromptSafety.DataOnlyNotice}}

            SOURCES THAT PASSED THE QUALITY CHECK (title, site, summary):
            {{PromptSafety.Wrap(list, "source list")}}

            Propose the attributes to record for every source:
            1. Start from the research questions: give each question one to four attributes that its answer needs.
            2. Generalise the values of each attribute over the sources above, as in keyword or open coding: short values that cover what the sources discuss, merged where they mean the same, with "Other" last when not every source will fit.
            3. Say whether a source can have several values (multiple) or exactly one.
            4. When the values cannot be listed in advance (names of tools, say), mark the attribute as open text and give no values.
            Use two to {{MaxValues}} values per attribute and at most {{MaxAttributes}} attributes in all.

            {{AnswerShape}}
            """;
        return await LlmJson.GetAsync<ModelMapAnswer>(chat, prompt, LlmJson.JsonMode(0.2), a => Problem(Clean(a.Attributes), plan));
    }

    private const string AnswerShape = """
        Respond ONLY with a valid minified JSON object of this shape:
        {"attributes":[{"name":"Practice","question":"RQ1","description":"One sentence.","values":["Retrieval","Summarisation","Other"],"multiple":true,"open":false}]}
        """;
}

/// <summary>The model's proposed map.</summary>
public sealed class ModelMapAnswer
{
    public List<MapAttribute> Attributes { get; set; } = new();
}

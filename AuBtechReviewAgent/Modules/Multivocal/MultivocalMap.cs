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
/// Builds the systematic map of a multivocal run (MLR block F, G12). The model proposes initial attributes from the
/// research questions and generalises their values over the sources that passed the quality check, as in keyword or
/// open coding; the reviewer then edits the map, each edit kept as a new version, and fixes it before extraction.
/// The run goes from "Assessed" through "Mapping" to "Mapped".
/// </summary>
public sealed partial class MultivocalMapper
{
    public const string MapFile = "multivocal-map.json";
    public const string StageMapping = "Mapping";
    public const string StageMapped = "Mapped";
    public const string PromptVersion = "grey-map-v4";

    public const int MaxAttributes = 20;

    /// <summary>The model may propose at most this many attributes per research question; the reviewer may add more.</summary>
    public const int MaxModelAttributesPerQuestion = 3;
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
            var proposed = Clean(answer.Attributes);
            // A question the map does not serve is said so in the map's notes, and the paper says it is not answered.
            foreach (var (number, _) in planned.Plan.Numbered().Where(q => !proposed.Any(a => !IsKnownFact(a) && a.Serves(q.Number))))
                file.Notes.Add($"{number} has no attribute in the model's map, so the extraction records nothing for it.");
            foreach (var known in proposed.Where(IsKnownFact))
                file.Notes.Add($"\"{known.Name}\" ({known.Question}) was left out of the model's map: it is known for every source and recorded by code.");
            file.Versions.Add(new MapVersion { Number = 1, CreatedUtc = DateTime.UtcNow, By = "model", Attributes = proposed.Where(a => !IsKnownFact(a)).ToList() });

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

    /// <summary>The map prompt: attributes from the questions, values generalised over the sources.</summary>
    public static async Task<ModelMapAnswer> AskAsync(IChatCompletionService chat, MultivocalPlan plan, IReadOnlyList<GreyRecord> sources)
    {
        string questions = string.Join("\n", plan.Numbered().Select(q => $"{q.Number} ({MultivocalGuidelines.FindType(q.Question.Type)?.Name ?? "not set"}): {q.Question.Text}"));
        string list = string.Join("\n", sources.Select((s, i) =>
            $"{i + 1}. {s.Title} ({s.Site}{(s.Kind == MultivocalSearcher.FormalKind ? ", academic" : "")}): {(s.Summary.Length > 300 ? s.Summary[..300] + "…" : s.Summary)}"));
        var prompt = $$"""
            You build the systematic map (classification scheme) of a multivocal literature review, following Garousi et al. (2019, G12 and Table 10).
            Topic: {{PromptSafety.Wrap(plan.Topic, "review topic")}}

            RESEARCH QUESTIONS (from the reviewer):
            {{PromptSafety.Wrap(questions, "research questions")}}

            {{PromptSafety.ReviewerInputNotice}}
            {{PromptSafety.DataOnlyNotice}}

            SOURCES THAT PASSED THE QUALITY CHECK (title, site, summary):
            {{PromptSafety.Wrap(list, "source list")}}

            Propose the attributes to record for every source:
            1. The attributes come from the research questions: give each question one to {{MaxModelAttributesPerQuestion}} attributes, only what its answer needs. Do not add attributes for other topics the sources happen to discuss.
            2. The values come from the sources: generalise them over the sources above, as in keyword or open coding, with short values merged where they mean the same, and "Other" last when not every source will fit.
            3. Each value belongs to one attribute only: do not make an attribute out of what is already a value of another (if "Memory" is a practice, do not add a separate memory attribute).
            4. Say whether a source can have several values (multiple) or exactly one.
            5. When the values cannot be listed in advance (names of tools, say), mark the attribute as open text and give no values.
            6. Do not add "Not specified", "Unknown" or similar values: "not stated" is recorded for every attribute automatically.
            7. Do not add attributes for what is already known about every source: {{string.Join(", ", KnownFacts.Select(f => f.Name.ToLowerInvariant()))}}, and the source's producer.
            8. Name each attribute in plain words with spaces ("Tool or framework", not "ToolOrFramework").
            9. Every research question needs an attribute. A question that only asks how often, or whether, the values of another question's attribute occur (such as "how often is each practice mentioned?") gets no attribute of its own: add its number to that attribute's "alsoFor" list instead.
            Use two to {{MaxValues}} values per attribute.

            {{AnswerShape}}
            """;
        return await LlmJson.GetAsync<ModelMapAnswer>(chat, prompt, LlmJson.JsonMode(0.2), a => ModelProblem(Clean(a.Attributes), plan));
    }

    private const string AnswerShape = """
        Respond ONLY with a valid minified JSON object of this shape:
        {"attributes":[{"name":"Practice","question":"RQ1","alsoFor":[],"description":"One sentence.","values":["Retrieval","Summarisation","Other"],"multiple":true,"open":false}]}
        """;
}

/// <summary>The model's proposed map.</summary>
public sealed class ModelMapAnswer
{
    public List<MapAttribute> Attributes { get; set; } = new();
}

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

    /// <summary>What code changed in the model's proposal, such as an attribute for a known fact that was left out.</summary>
    public List<string> Notes { get; set; } = new();
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
    public const string PromptVersion = "grey-map-v2";

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

    /// <summary>
    /// Values that only say the source does not state it. Every attribute can be "not stated" for a source, decided in
    /// code during extraction, so these are never values of their own (seen in a live run: seven attributes had one).
    /// </summary>
    public static readonly IReadOnlySet<string> NotStatedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "not specified", "unspecified", "not stated", "not mentioned", "not given", "not reported", "not applicable", "unknown", "n/a", "na", "none specified",
    };

    /// <summary>
    /// The facts the run already knows about every source, recorded by code for each one at extraction; the map never
    /// asks the model for them.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Description)> KnownFacts = new[]
    {
        ("Kind of source", "Blog post, Q&A, code repository, report, thesis …, from the search."),
        ("Literature", "Grey or formal (academic), from the pool the source was found in."),
        ("Site", "Where it was published."),
        ("Date", "When it was published, when the search gave a date."),
        ("Quality points", "Its score on the quality checklist, out of 20."),
    };

    /// <summary>The known facts of one source, in the order of <see cref="KnownFacts"/>.</summary>
    public static IReadOnlyList<(string Name, string Value)> KnownFactsFor(GreyRecord record, GreyQuality? quality) => new[]
    {
        ("Kind of source", MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == record.Kind).Label ?? record.Kind),
        ("Literature", "Grey"),
        ("Site", record.Site),
        ("Date", record.Published is DateTime d ? d.ToString("yyyy-MM-dd") : "not given"),
        ("Quality points", quality is { Outcome: not "Not assessed" } q ? $"{q.Points:0.#} of {GreyQualityChecklist.MaxPoints}" : "not assessed"),
    };

    /// <summary>"ToolOrFramework" becomes "Tool or framework"; a name with spaces is kept as written.</summary>
    public static string PlainName(string name)
    {
        string n = name.Trim();
        if (n.Contains(' ') || !System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Z][a-z]+([A-Z][a-z]+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))) return n;
        var words = System.Text.RegularExpressions.Regex.Matches(n, "[A-Z][a-z]+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Value).ToList();
        return string.Join(" ", words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    /// <summary>
    /// Trims every text, makes names plain words, drops empty and repeated values and values that only say "not stated",
    /// and keeps no values on an open attribute.
    /// </summary>
    public static List<MapAttribute> Clean(IEnumerable<MapAttribute> attributes) => attributes.Select(a => new MapAttribute
    {
        Name = PlainName(ReviewInputGuard.Normalize(a.Name ?? "", singleLine: true)),
        Question = (a.Question ?? "").Trim().ToUpperInvariant(),
        Description = ReviewInputGuard.Normalize(a.Description ?? "", singleLine: true),
        Open = a.Open,
        Multiple = a.Multiple,
        Values = a.Open ? new() : (a.Values ?? new())
            .Select(v => ReviewInputGuard.Normalize(v ?? "", singleLine: true))
            .Where(v => v.Length > 0 && !NotStatedValues.Contains(v.TrimEnd('.')))
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

    /// <summary>
    /// Whether an attribute only records a fact the run already knows for every source (its kind, grey or formal, site,
    /// date or quality). Such an attribute is dropped from the model's map in code rather than sent back: a question
    /// such as "how often in grey and in academic sources?" makes the model add one however it is told (seen in a
    /// live run, twice in a row), and the known fact answers it anyway.
    /// </summary>
    public static bool IsKnownFact(MapAttribute attribute) => System.Text.RegularExpressions.Regex.IsMatch(attribute.Name,
        @"^(source ?type|type of source|kind of source|grey or academic|grey or formal|literature( type)?|publication (date|site|year)|date|site|quality( points| score)?)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// What is wrong with the model's map, beyond <see cref="Problem"/>, once the attributes for known facts are
    /// dropped: more than three attributes for one question, or a value that is also another attribute's value.
    /// </summary>
    public static string? ModelProblem(IReadOnlyList<MapAttribute> proposed, MultivocalPlan plan)
    {
        var attributes = proposed.Where(a => !IsKnownFact(a)).ToList();
        if (Problem(attributes, plan) is { } problem) return problem;
        var crowded = attributes.GroupBy(a => a.Question).FirstOrDefault(g => g.Count() > MaxModelAttributesPerQuestion);
        if (crowded != null) return $"{crowded.Key} has {crowded.Count()} attributes; give each question at most {MaxModelAttributesPerQuestion}.";
        var shared = attributes
            .SelectMany(a => a.Values.Where(v => !v.Equals("Other", StringComparison.OrdinalIgnoreCase)).Select(v => (Value: v, a.Name)))
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Select(x => x.Name).Distinct().Count() > 1);
        return shared != null ? $"\"{shared.Key}\" is a value of both {string.Join(" and ", shared.Select(x => $"\"{x.Name}\"").Distinct())}; keep each value in one attribute." : null;
    }

    /// <summary>The map prompt: attributes from the questions, values generalised over the sources.</summary>
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
            1. The attributes come from the research questions: give each question one to {{MaxModelAttributesPerQuestion}} attributes, only what its answer needs. Do not add attributes for other topics the sources happen to discuss.
            2. The values come from the sources: generalise them over the sources above, as in keyword or open coding, with short values merged where they mean the same, and "Other" last when not every source will fit.
            3. Each value belongs to one attribute only: do not make an attribute out of what is already a value of another (if "Memory" is a practice, do not add a separate memory attribute).
            4. Say whether a source can have several values (multiple) or exactly one.
            5. When the values cannot be listed in advance (names of tools, say), mark the attribute as open text and give no values.
            6. Do not add "Not specified", "Unknown" or similar values: "not stated" is recorded for every attribute automatically.
            7. Do not add attributes for what is already known about every source: {{string.Join(", ", KnownFacts.Select(f => f.Name.ToLowerInvariant()))}}, and the source's producer.
            8. Name each attribute in plain words with spaces ("Tool or framework", not "ToolOrFramework").
            Use two to {{MaxValues}} values per attribute.

            {{AnswerShape}}
            """;
        return await LlmJson.GetAsync<ModelMapAnswer>(chat, prompt, LlmJson.JsonMode(0.2), a => ModelProblem(Clean(a.Attributes), plan));
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

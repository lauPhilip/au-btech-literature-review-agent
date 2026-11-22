using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The synthesis of a multivocal run (MLR block F, G13): for each research question, the extracted findings are
/// grouped into themes by the model; every finding goes into a theme or is listed as unassigned with a reason, and
/// the tensions between sources are named. What each theme rests on (how many sources, of which outlet tiers and
/// quality, grey or formal) is counted in code, never by the model. Formal sources join once their search is built;
/// until then every theme says it rests on grey literature only. The run goes from "Extracted" to "Synthesised".
/// </summary>
public sealed partial class MultivocalSynthesiser
{
    public const string SynthesisFile = "multivocal-synthesis.json";
    public const string StageSynthesised = "Synthesised";
    public const string PromptVersion = "grey-synthesis-v4";
    public const int MaxThemesPerQuestion = 6;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly MultivocalQualityAssessor _quality;
    private readonly MultivocalMapper _mapper;
    private readonly MultivocalExtractor _extractor;
    private readonly MultivocalPages _pages;
    private readonly Func<IChatCompletionService> _chat;
    private readonly string _model;

    /// <summary>A synthesiser that asks <paramref name="chat"/>'s model to group the findings of each question.</summary>
    private readonly ReviewCache _cache;

    public MultivocalSynthesiser(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, MultivocalQualityAssessor quality,
        MultivocalMapper mapper, MultivocalExtractor extractor, MultivocalPages pages, Func<IChatCompletionService> chat, string model = "", ReviewCache? cache = null)
    {
        _cache = cache ?? ReviewCache.Disabled;
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _quality = quality;
        _mapper = mapper;
        _extractor = extractor;
        _pages = pages;
        _chat = chat;
        _model = model;
    }

    public MultivocalSynthesisFile? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), SynthesisFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalSynthesisFile>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>Synthesises an extracted run. Needs the run's edit key.</summary>
    public async Task<MultivocalSynthesisFile> SynthesiseAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can synthesise it.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");
        var extraction = _extractor.Load(runId) ?? throw new InvalidOperationException("Extract the sources first.");
        var map = _mapper.Load(runId);
        var fixedMap = map?.FixedVersion is int number ? map.Versions.First(v => v.Number == number) : throw new InvalidOperationException("The map is not fixed.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run is being synthesised already.");
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage != MultivocalExtractor.StageExtracted) throw new InvalidOperationException("This run has been synthesised already.");

            var records = (_searcher.LoadLedger(runId)?.Sources ?? new()).ToDictionary(s => MultivocalSearcher.AddressKey(s.Record.Url), s => s.Record);
            var points = (_quality.Load(runId)?.Sources ?? new()).ToDictionary(q => q.Address, q => q.Points);
            var file = new MultivocalSynthesisFile
            {
                RunId = runId,
                SynthesisedUtc = DateTime.UtcNow,
                PromptVersion = PromptVersion,
                Model = _model,
            };
            // Every quote is checked again against the kept page text before it is used; one that is no longer there
            // (the kept text was changed or removed) is left out, and the note says how many.
            var extracted = Findings(extraction, fixedMap, records, points);
            var checkedFindings = extracted.Where(f => _pages.CheckQuote(runId, f.Address, f.Quote) == "found").ToList();
            if (checkedFindings.Count < extracted.Count)
                file.Notes.Add($"{extracted.Count - checkedFindings.Count} extracted value(s) were left out because their quote is no longer in the kept page text.");
            // A value that only says the source does not state it ("Not addressed") is not a finding.
            file.Findings = checkedFindings.Where(f => !IsNotStated(f.Value)).ToList();
            if (file.Findings.Count < checkedFindings.Count)
                file.Notes.Add($"{checkedFindings.Count - file.Findings.Count} extracted value(s) only say the source does not address the question, so they are not findings.");
            var open = fixedMap.Attributes.Where(a => a.Open).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var chat = _chat();
            // A quote can be on the page without saying what its value claims (seen in a live run: a forecast of
            // cancelled projects coded as evidence on retrieval). Every value is checked against its quote, one call
            // per source; a value judged not supported is left out of the findings and listed with the reason.
            progress?.Report($"Checking that each of {file.Findings.Count} quotes supports its value");
            var (supported, unsupported, uncheckedCount) = await CheckSupportAsync(chat, file.Findings, open, _cache, _model);
            file.Findings = supported;
            file.Unsupported = unsupported;
            if (unsupported.Count > 0)
                file.Notes.Add($"{unsupported.Count} extracted value(s) were left out because their quote does not support the value; they are listed with the reason.");
            if (uncheckedCount > 0)
                file.Notes.Add($"The support of {uncheckedCount} extracted value(s) could not be checked (the model's answer failed); they are kept.");
            foreach (var (question, q) in planned.Plan.Numbered())
            {
                var findings = file.Findings.Where(f => f.Question == question).ToList();
                if (findings.Count == 0)
                {
                    // A question answered by another question's attribute (alsoFor) is answered by counting its values.
                    bool countedElsewhere = fixedMap.Attributes.Any(a => a.AlsoFor.Contains(question) && file.Findings.Any(f => f.Attribute == a.Name));
                    if (!countedElsewhere) file.Unanswered.Add(question);
                    continue;
                }
                var groups = ValueGroups(findings);
                file.NotCoded.AddRange(groups.Where(IsOther).Select(g => new NotCodedValue { Question = question, Attribute = g.Attribute, Findings = g.Findings, Sources = g.Sources }));
                groups = groups.Where(g => !IsOther(g)).ToList();
                if (groups.Count == 0) continue;
                progress?.Report($"{question}: {findings.Count} findings in {groups.Count} values; naming the themes");
                await SynthesiseQuestionAsync(chat, planned.Plan.Topic, question, q.Text, groups, findings, open, file, progress);
            }

            string folder = _runs.FolderOf(runId);
            await SafeFile.WriteAllTextAsync(Path.Join(folder, SynthesisFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageSynthesised });
            return file;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    /// <summary>
    /// Every extracted value with its quote becomes a finding, numbered by source ("F3.2"), labelled in code with
    /// its source's kind, outlet tier and quality points.
    /// </summary>
    public static List<SynthesisFinding> Findings(MultivocalExtractionFile extraction, MapVersion map,
        IReadOnlyDictionary<string, GreyRecord> records, IReadOnlyDictionary<string, double> points)
    {
        var findings = new List<SynthesisFinding>();
        int sourceNumber = 0;
        foreach (var source in extraction.Sources.Where(s => s.Error == null))
        {
            sourceNumber++;
            string kind = records.TryGetValue(source.Address, out var r) ? r.Kind : "";
            int index = 0;
            foreach (var attribute in source.Attributes)
            {
                string question = map.Attributes.FirstOrDefault(a => a.Name == attribute.Attribute)?.Question ?? "";
                foreach (var value in attribute.Values)
                    findings.Add(new SynthesisFinding
                    {
                        Id = $"F{sourceNumber}.{++index}",
                        Question = question,
                        Attribute = attribute.Attribute,
                        Value = value.Value,
                        Quote = value.Quote,
                        Address = source.Address,
                        Title = source.Title,
                        Kind = kind,
                        Tier = TierOf(kind),
                        QualityPoints = points.GetValueOrDefault(source.Address),
                    });
            }
        }
        return findings;
    }

    /// <summary>Whether a value only says the source does not state it, such as "Not addressed".</summary>
    public static bool IsNotStated(string value)
    {
        string v = value.Trim().TrimEnd('.');
        return MultivocalMapper.NotStatedValues.Contains(v) || v.Equals("not addressed", StringComparison.OrdinalIgnoreCase) || v.Equals("not discussed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a value group is the map's "Other", which the model does not theme.</summary>
    public static bool IsOther(ValueGroup group) => group.Value.Equals("Other", StringComparison.OrdinalIgnoreCase);

    /// <summary>The outlet tier (1, 2 or 3) of a kind of source, from the score of checklist item 8.1.</summary>
    public static int TierOf(string kind) => GreyQualityChecklist.OutletTier(kind).Score switch { 1 => 1, 0.5 => 2, _ => 3 };

    /// <summary>What a theme rests on, counted from its findings' sources; the model's counts are never used.</summary>
    public static SynthesisTheme Count(SynthesisTheme theme, IReadOnlyList<SynthesisFinding> findings)
    {
        var sources = findings.Where(f => theme.Findings.Contains(f.Id)).GroupBy(f => f.Address).Select(g => g.First()).ToList();
        theme.Sources = sources.Count;
        theme.GreySources = sources.Count(s => s.Literature == "grey");
        theme.FormalSources = sources.Count(s => s.Literature == "formal");
        theme.FirstTier = sources.Count(s => s.Literature == "grey" && s.Tier == 1);
        theme.SecondTier = sources.Count(s => s.Literature == "grey" && s.Tier == 2);
        theme.ThirdTier = sources.Count(s => s.Literature == "grey" && s.Tier == 3);
        theme.MeanQuality = sources.Count == 0 ? 0 : Math.Round(sources.Average(s => s.QualityPoints), 1);
        theme.EvidenceLabel = theme.FormalSources > 0 && theme.GreySources > 0 ? "grey and formal literature"
            : theme.FormalSources > 0 ? "formal literature only"
            : theme.Sources > 0 && theme.ThirdTier == theme.Sources ? "3rd-tier grey literature only"
            : "grey literature only";
        return theme;
    }

    /// <summary>Step 1: the themes of a question, named and described, from its value groups.</summary>
    public static async Task<ModelThemeList> AskThemesAsync(IChatCompletionService chat, string topic, string question, string questionText, IReadOnlyList<ValueGroup> groups)
    {
        string list = string.Join("\n", groups.Select(g => $"{g.Attribute}: {g.Value} ({g.Sources} source{(g.Sources == 1 ? "" : "s")})"));
        var prompt = $$"""
            You synthesise the findings of a multivocal literature review for one research question (Garousi et al. 2019, G13), by qualitative coding.
            Topic: {{PromptSafety.Wrap(topic, "review topic")}}
            Research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}

            {{PromptSafety.DataOnlyNotice}}

            WHAT THE SOURCES SAY (attribute: value, and how many sources say it):
            {{PromptSafety.Wrap(list, "values")}}

            Name one to {{MaxThemesPerQuestion}} themes that together answer the question from these values. Name each theme in a few plain words and describe in one sentence what it covers. Do not say how many sources support a theme, or whether it is true: that is counted and judged separately.

            Respond ONLY with a valid minified JSON object of this shape:
            {"themes":[{"name":"Theme name","description":"One sentence."}]}
            """;
        return await LlmJson.GetAsync<ModelThemeList>(chat, prompt, LlmJson.JsonMode(0.2), ModelThemeList.Problem, repairAttempts: 2);
    }

    /// <summary>Step 2: one batch of value groups placed in the themes; every group in a theme or none, with a reason.</summary>
    public static async Task<ModelPlacement> AskPlacementAsync(IChatCompletionService chat, string question, string questionText, ModelThemeList themes, IReadOnlyList<ValueGroup> batch)
    {
        string themeList = string.Join("\n", themes.Themes.Select(t => $"- {t.Name.Trim()}: {t.Description.Trim()}"));
        string list = string.Join("\n", batch.Select(g => $"{g.Id} {g.Attribute}: {g.Value} ({g.Sources} source{(g.Sources == 1 ? "" : "s")}); for example {string.Join(" / ", g.Samples)}"));
        var prompt = $$"""
            You place the findings of a multivocal literature review in its themes, for research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}

            THEMES:
            {{PromptSafety.Wrap(themeList, "themes")}}

            {{PromptSafety.DataOnlyNotice}}

            VALUES TO PLACE (id, attribute: value, sources, example quotes):
            {{PromptSafety.Wrap(list, "values")}}

            Place every value above: give the names of the themes it belongs to (one or more, written exactly as above), or no theme with the reason it fits none.

            Respond ONLY with a valid minified JSON object of this shape, one entry per value:
            {"placements":[{"group":"V1","themes":["Theme name"],"reason":""},{"group":"V2","themes":[],"reason":"Why it fits no theme."}]}
            """;
        var ids = batch.Select(g => g.Id).ToHashSet();
        var names = themes.Themes.Select(t => t.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await LlmJson.GetAsync<ModelPlacement>(chat, prompt, LlmJson.JsonMode(0.0), a => ModelPlacement.Problem(a, ids, names), repairAttempts: 2);
    }

    /// <summary>Step 3: the tensions in one theme, between values of different sources, from sample quotes.</summary>
    public static async Task<ModelTensions> AskTensionsAsync(IChatCompletionService chat, string question, string questionText, string theme,
        IReadOnlyList<ValueGroup> groups, IReadOnlyList<SynthesisFinding> findings)
    {
        string list = string.Join("\n", groups.Select(g => $"{g.Id} {g.Attribute}: {g.Value} ({g.Sources} source{(g.Sources == 1 ? "" : "s")}); for example {string.Join(" / ", g.Samples)}"));
        var prompt = $$"""
            In a multivocal literature review, research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}
            Theme: {{PromptSafety.Wrap(theme, "theme")}}

            {{PromptSafety.DataOnlyNotice}}

            VALUES IN THIS THEME (id, attribute: value, sources, example quotes):
            {{PromptSafety.Wrap(list, "values")}}

            Name the tensions: pairs of values whose sources disagree or contradict each other about how to do something or what works, with one sentence on how, using the values' names, not their ids. Values that are merely different, or different options, are not tensions. Only real disagreements; none is fine.

            Respond ONLY with a valid minified JSON object of this shape:
            {"tensions":[{"groupA":"V1","groupB":"V4","description":"One sentence."}]}
            """;
        return await LlmJson.GetAsync<ModelTensions>(chat, prompt, LlmJson.JsonMode(0.2), _ => null);
    }

    /// <summary>
    /// The model's tensions as pairs of findings from different sources, one from each value; a tension that names a
    /// value outside the theme, or whose two values come from the same single source, is left out with a note.
    /// </summary>
    public static List<SynthesisTension> TensionsFor(ModelTensions answer, IReadOnlyList<ValueGroup> groups, IReadOnlyList<SynthesisFinding> findings, out List<string> notes)
    {
        notes = new List<string>();
        var byId = groups.ToDictionary(g => g.Id);
        var byFinding = findings.ToDictionary(f => f.Id);
        var tensions = new List<SynthesisTension>();
        int outside = 0, oneSource = 0;
        foreach (var t in answer.Tensions)
        {
            if (!byId.TryGetValue(t.GroupA.Trim(), out var a) || !byId.TryGetValue(t.GroupB.Trim(), out var b) || a == b)
            {
                outside++;
                continue;
            }
            var pair = a.Findings.SelectMany(fa => b.Findings.Select(fb => (A: byFinding[fa], B: byFinding[fb]))).FirstOrDefault(x => x.A.Address != x.B.Address);
            if (pair.A == null)
            {
                oneSource++;
                continue;
            }
            // The model sees the values by id; the reader sees their names.
            string description = ValueId.Replace(t.Description.Trim(), m => byId.TryGetValue(m.Value, out var g) ? $"\"{g.Value}\"" : "another value");
            tensions.Add(new SynthesisTension { FindingA = pair.A.Id, FindingB = pair.B.Id, Description = $"{a.Value} against {b.Value}. {description}" });
        }
        if (outside > 0) notes.Add($"{outside} tension(s) named a value outside the theme; left out.");
        if (oneSource > 0) notes.Add($"{oneSource} tension(s) were between values of one source only; left out.");
        return tensions;
    }
}

/// <summary>The findings of one question with the same attribute and value, grouped in code.</summary>
public sealed class ValueGroup
{
    public string Id { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public List<string> Findings { get; set; } = new();
    public int Sources { get; set; }
    public List<string> Samples { get; set; } = new();

    public string Label => $"{Attribute}: {Value} ({Findings.Count} finding{(Findings.Count == 1 ? "" : "s")})";
}

/// <summary>The model's themes for one question.</summary>
public sealed class ModelThemeList
{
    public List<ModelThemeName> Themes { get; set; } = new();

    /// <summary>Null when there are one to six themes, each named once.</summary>
    public static string? Problem(ModelThemeList a)
    {
        if (a.Themes.Count is 0 or > MultivocalSynthesiser.MaxThemesPerQuestion) return $"give one to {MultivocalSynthesiser.MaxThemesPerQuestion} themes.";
        if (a.Themes.Any(t => string.IsNullOrWhiteSpace(t.Name))) return "every theme needs a name.";
        var repeated = a.Themes.GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated != null) return $"two themes are called \"{repeated.Key}\".";
        var counting = a.Themes.FirstOrDefault(t => Counts.IsMatch(t.Name) || Counts.IsMatch(t.Description));
        return counting != null ? $"the theme \"{counting.Name.Trim()}\" says how many sources support it; describe only what it covers, the sources are counted in code." : null;
    }

    /// <summary>How many sources say something ("the majority of sources", "most practitioners", "12 sources"): counted in code, never by the model.</summary>
    private static readonly Regex Counts = new(
        @"\b(majority|minority|consensus)\b|\b(most|many|few|several|all|some|\d+)\s+(of\s+the\s+)?(sources|studies|authors|practitioners|papers|posts|repositories|articles)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>One theme's name and description.</summary>
public sealed class ModelThemeName
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>The model's placement of one batch of value groups.</summary>
public sealed class ModelPlacement
{
    public List<ModelPlaced> Placements { get; set; } = new();

    /// <summary>Null when every group of the batch is placed once, in named themes only, or in none with a reason.</summary>
    public static string? Problem(ModelPlacement a, IReadOnlySet<string> groups, IReadOnlySet<string> themes)
    {
        var unknown = a.Placements.Select(p => p.Group.Trim()).FirstOrDefault(g => !groups.Contains(g));
        if (unknown != null) return $"\"{unknown}\" is not one of the values to place.";
        var missing = groups.Where(g => a.Placements.All(p => p.Group.Trim() != g)).ToList();
        if (missing.Count > 0) return $"values {string.Join(", ", missing)} are not placed; give every value its themes, or none with the reason.";
        var badTheme = a.Placements.SelectMany(p => p.Themes).Select(t => t.Trim()).FirstOrDefault(t => !themes.Contains(t));
        if (badTheme != null) return $"\"{badTheme}\" is not one of the themes; use the names exactly as given.";
        if (a.Placements.Any(p => p.Themes.Count == 0 && string.IsNullOrWhiteSpace(p.Reason))) return "a value in no theme needs the reason.";
        return null;
    }
}

/// <summary>Where one value group goes.</summary>
public sealed class ModelPlaced
{
    public string Group { get; set; } = "";
    public List<string> Themes { get; set; } = new();
    public string? Reason { get; set; }
}

/// <summary>The model's tensions in one theme.</summary>
public sealed class ModelTensions
{
    public List<ModelTension> Tensions { get; set; } = new();
}

/// <summary>Two value groups that disagree.</summary>
public sealed class ModelTension
{
    public string GroupA { get; set; } = "";
    public string GroupB { get; set; } = "";
    public string Description { get; set; } = "";
}

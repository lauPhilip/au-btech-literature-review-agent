using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// One finding: an extracted value of one source, with the quote from its kept page. Its labels (kind, outlet tier,
/// quality points, grey or formal) are decided in code, so the synthesis can say what kind of evidence a theme rests on.
/// </summary>
public sealed class SynthesisFinding
{
    /// <summary>"F3.2": the second finding of the third source.</summary>
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";

    /// <summary>"grey" or "formal".</summary>
    public string Literature { get; set; } = "grey";

    /// <summary>1, 2 or 3: the outlet tier of Table 7 (1st tier scores 1 point on item 8.1).</summary>
    public int Tier { get; set; }
    public double QualityPoints { get; set; }

    /// <summary>What the quote was checked against: always the kept page text, never the live page.</summary>
    public string EvidenceBasis { get; set; } = "page text";
}

/// <summary>Two findings from different sources that disagree, as the model read them.</summary>
public sealed class SynthesisTension
{
    public string FindingA { get; set; } = "";
    public string FindingB { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>One theme of one research question, with the evidence it rests on counted in code.</summary>
public sealed class SynthesisTheme
{
    public string Question { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Findings { get; set; } = new();
    public List<SynthesisTension> Tensions { get; set; } = new();

    // Decided in code from the findings:
    public int Sources { get; set; }
    public int GreySources { get; set; }
    public int FormalSources { get; set; }
    public int FirstTier { get; set; }
    public int SecondTier { get; set; }
    public int ThirdTier { get; set; }
    public double MeanQuality { get; set; }

    /// <summary>"grey and formal literature", "grey literature only" or "3rd-tier grey literature only".</summary>
    public string EvidenceLabel { get; set; } = "";
}

/// <summary>A finding the model put in no theme, with its reason.</summary>
public sealed class UnassignedFinding
{
    public string Finding { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>The synthesis of a multivocal run (multivocal-synthesis.json), which is in the run archive.</summary>
public sealed class MultivocalSynthesisFile
{
    public Guid RunId { get; set; }
    public DateTime SynthesisedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public List<SynthesisFinding> Findings { get; set; } = new();
    public List<SynthesisTheme> Themes { get; set; } = new();
    public List<UnassignedFinding> Unassigned { get; set; } = new();

    /// <summary>What every quote was checked against: the kept page text, never the live page.</summary>
    public string EvidenceBasis { get; set; } = "page text";

    /// <summary>Research questions with no finding at all, so the review cannot answer them from these sources.</summary>
    public List<string> Unanswered { get; set; } = new();

    /// <summary>A note per question, such as that no formal source has been searched yet.</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// The synthesis of a multivocal run (MLR block F, G13): for each research question, the extracted findings are
/// grouped into themes by the model; every finding goes into a theme or is listed as unassigned with a reason, and
/// the tensions between sources are named. What each theme rests on (how many sources, of which outlet tiers and
/// quality, grey or formal) is counted in code, never by the model. Formal sources join once their search is built;
/// until then every theme says it rests on grey literature only. The run goes from "Extracted" to "Synthesised".
/// </summary>
public sealed class MultivocalSynthesiser
{
    public const string SynthesisFile = "multivocal-synthesis.json";
    public const string StageSynthesised = "Synthesised";
    public const string PromptVersion = "grey-synthesis-v2";
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
    public MultivocalSynthesiser(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, MultivocalQualityAssessor quality,
        MultivocalMapper mapper, MultivocalExtractor extractor, MultivocalPages pages, Func<IChatCompletionService> chat, string model = "")
    {
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
            file.Findings = extracted.Where(f => _pages.CheckQuote(runId, f.Address, f.Quote) == "found").ToList();
            if (file.Findings.Count < extracted.Count)
                file.Notes.Add($"{extracted.Count - file.Findings.Count} extracted value(s) were left out because their quote is no longer in the kept page text.");
            file.Notes.Add("No formal (academic) literature has been searched yet, so every theme rests on grey literature only.");

            var chat = _chat();
            foreach (var (question, q) in planned.Plan.Numbered())
            {
                var findings = file.Findings.Where(f => f.Question == question).ToList();
                if (findings.Count == 0)
                {
                    file.Unanswered.Add(question);
                    continue;
                }
                var groups = ValueGroups(findings);
                progress?.Report($"{question}: {findings.Count} findings in {groups.Count} values; naming the themes");
                await SynthesiseQuestionAsync(chat, planned.Plan.Topic, question, q.Text, groups, findings, file, progress);
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

    /// <summary>How many value groups are placed in one call: few enough that the model places every one.</summary>
    public const int GroupsPerCall = 30;

    /// <summary>
    /// One question, in three steps that each stay small: the model names the themes from the question's value groups;
    /// it places the groups in the themes a batch at a time (every group placed, or a batch that still fails after two
    /// retries is listed as not placed, with a note); and for each theme with groups from two sources or more it names
    /// the tensions, from sample quotes. Every finding of a group goes into its group's themes.
    /// </summary>
    public static async Task SynthesiseQuestionAsync(IChatCompletionService chat, string topic, string question, string questionText,
        List<ValueGroup> groups, List<SynthesisFinding> findings, MultivocalSynthesisFile file, IProgress<string>? progress)
    {
        var named = await AskThemesAsync(chat, topic, question, questionText, groups);
        var placement = named.Themes.ToDictionary(t => t.Name.Trim(), _ => new List<ValueGroup>(), StringComparer.OrdinalIgnoreCase);

        for (int at = 0; at < groups.Count; at += GroupsPerCall)
        {
            var batch = groups.Skip(at).Take(GroupsPerCall).ToList();
            progress?.Report($"{question}: placing values {at + 1}–{at + batch.Count} of {groups.Count}");
            ModelPlacement placed;
            try { placed = await AskPlacementAsync(chat, question, questionText, named, batch); }
            catch (LlmOutputException ex)
            {
                // A batch the model cannot place after two retries is kept on file as not placed, not lost.
                file.Notes.Add($"{question}: {batch.Count} value(s) could not be placed by the model ({ex.Message}).");
                file.Unassigned.AddRange(batch.Select(g => new UnassignedFinding { Finding = g.Label, Reason = "Not placed: the model's answers failed the check." }));
                continue;
            }
            foreach (var p in placed.Placements)
            {
                var group = batch.First(g => g.Id == p.Group.Trim());
                if (p.Themes.Count == 0)
                    file.Unassigned.Add(new UnassignedFinding { Finding = group.Label, Reason = string.IsNullOrWhiteSpace(p.Reason) ? "In no theme." : p.Reason.Trim() });
                foreach (var name in p.Themes) placement[name.Trim()].Add(group);
            }
        }

        foreach (var theme in named.Themes)
        {
            var inTheme = placement[theme.Name.Trim()];
            if (inTheme.Count == 0)
            {
                file.Notes.Add($"{question}: the theme \"{theme.Name.Trim()}\" got no values and was left out.");
                continue;
            }
            var result = Count(new SynthesisTheme
            {
                Question = question,
                Name = theme.Name.Trim(),
                Description = theme.Description.Trim(),
                Findings = inTheme.SelectMany(g => g.Findings).Distinct().ToList(),
            }, findings);
            if (inTheme.Count >= 2 && result.Sources >= 2)
            {
                try
                {
                    var tensions = await AskTensionsAsync(chat, question, questionText, result.Name, inTheme, findings);
                    result.Tensions = TensionsFor(tensions, inTheme, findings, out var notes);
                    file.Notes.AddRange(notes.Select(n => $"{question}, \"{result.Name}\": {n}"));
                }
                catch (LlmOutputException ex)
                {
                    // Tensions are an extra: a failed answer leaves the theme without them and says so.
                    file.Notes.Add($"{question}, \"{result.Name}\": the tensions could not be read ({ex.Message}).");
                }
            }
            file.Themes.Add(result);
        }
    }

    /// <summary>
    /// The findings of one question grouped by attribute and value ("Practice: Retrieval"), in code: the map's values are
    /// already the first codes, so the model groups a few dozen values instead of hundreds of findings.
    /// </summary>
    public static List<ValueGroup> ValueGroups(IReadOnlyList<SynthesisFinding> findings) => findings
        .GroupBy(f => (f.Attribute, Value: f.Value.Trim().ToLowerInvariant()))
        .Select((g, i) => new ValueGroup
        {
            Id = $"V{i + 1}",
            Attribute = g.Key.Attribute,
            Value = g.First().Value.Trim(),
            Findings = g.Select(f => f.Id).ToList(),
            Sources = g.Select(f => f.Address).Distinct().Count(),
            Samples = g.GroupBy(f => f.Address).Select(s => s.First()).Take(2).Select(f => $"\"{Short(f.Quote)}\" ({f.Title})").ToList(),
        })
        .ToList();

    private static string Short(string quote) => quote.Length > 200 ? quote[..200] + "…" : quote;

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

            Name one to {{MaxThemesPerQuestion}} themes that together answer the question from these values. Name each theme in a few plain words and describe it in one sentence. Do not judge the sources: that is done separately.

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

            Name the tensions: pairs of values that disagree or contradict each other, with one sentence on how. Only real disagreements; none is fine.

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
        foreach (var t in answer.Tensions)
        {
            if (!byId.TryGetValue(t.GroupA.Trim(), out var a) || !byId.TryGetValue(t.GroupB.Trim(), out var b) || a == b)
            {
                notes.Add($"a tension ({t.GroupA}, {t.GroupB}) named a value outside the theme; left out.");
                continue;
            }
            var pair = a.Findings.SelectMany(fa => b.Findings.Select(fb => (A: byFinding[fa], B: byFinding[fb]))).FirstOrDefault(x => x.A.Address != x.B.Address);
            if (pair.A == null)
            {
                notes.Add($"a tension between \"{a.Value}\" and \"{b.Value}\" was within one source; left out.");
                continue;
            }
            tensions.Add(new SynthesisTension { FindingA = pair.A.Id, FindingB = pair.B.Id, Description = $"{a.Attribute}: {a.Value} against {b.Attribute}: {b.Value}. {t.Description.Trim()}" });
        }
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
        return repeated != null ? $"two themes are called \"{repeated.Key}\"." : null;
    }
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

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
    public const string PromptVersion = "grey-synthesis-v1";
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
                progress?.Report($"{question}: grouping {findings.Count} findings");
                var answer = await AskAsync(chat, planned.Plan.Topic, question, q.Text, findings);
                file.Notes.AddRange(ModelSynthesis.Tidy(answer, findings.ToDictionary(f => f.Id, f => f.Address)).Select(n => $"{question}: {n}"));
                foreach (var theme in answer.Themes)
                    file.Themes.Add(Count(new SynthesisTheme
                    {
                        Question = question,
                        Name = theme.Name.Trim(),
                        Description = theme.Description.Trim(),
                        Findings = theme.Findings.Select(id => id.Trim()).Distinct().ToList(),
                        Tensions = theme.Tensions.Select(t => new SynthesisTension { FindingA = t.FindingA.Trim(), FindingB = t.FindingB.Trim(), Description = t.Description.Trim() }).ToList(),
                    }, findings));
                file.Unassigned.AddRange(answer.Unassigned.Select(u => new UnassignedFinding { Finding = u.Finding.Trim(), Reason = u.Reason.Trim() }));
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

    /// <summary>The synthesis prompt for one research question: its findings grouped into themes, with the tensions.</summary>
    public static async Task<ModelSynthesis> AskAsync(IChatCompletionService chat, string topic, string question, string questionText, IReadOnlyList<SynthesisFinding> findings)
    {
        string list = string.Join("\n", findings.Select(f =>
            $"{f.Id} [{f.Attribute}: {f.Value}] from \"{f.Title}\": \"{(f.Quote.Length > 300 ? f.Quote[..300] + "…" : f.Quote)}\""));
        var prompt = $$"""
            You synthesise the findings of a multivocal literature review for one research question (Garousi et al. 2019, G13), by qualitative coding: group the findings into themes.
            Topic: {{PromptSafety.Wrap(topic, "review topic")}}
            Research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}

            {{PromptSafety.DataOnlyNotice}}

            FINDINGS (id, attribute and value, source, quote from the source's page):
            {{PromptSafety.Wrap(list, "findings")}}

            1. Group the findings into one to {{MaxThemesPerQuestion}} themes that answer the question. Name each theme in a few plain words and describe it in one sentence.
            2. Put every finding in at least one theme, or list it as unassigned with the reason.
            3. Name the tensions: pairs of findings from different sources that disagree, with one sentence on how. Only real disagreements; none is fine.
            Do not count sources or judge their quality: that is done separately.

            {{AnswerShape}}
            """;
        var ids = findings.ToDictionary(f => f.Id, f => f.Address);
        return await LlmJson.GetAsync<ModelSynthesis>(chat, prompt, LlmJson.JsonMode(0.2), a => ModelSynthesis.Problem(a, ids), repairAttempts: 2);
    }

    private const string AnswerShape = """
        Respond ONLY with a valid minified JSON object of this shape:
        {"themes":[{"name":"Theme name","description":"One sentence.","findings":["F1.1","F3.2"],"tensions":[{"findingA":"F1.1","findingB":"F3.2","description":"One sentence."}]}],"unassigned":[{"finding":"F2.1","reason":"One sentence."}]}
        """;
}

/// <summary>The model's themes for one research question.</summary>
public sealed class ModelSynthesis
{
    public List<ModelTheme> Themes { get; set; } = new();
    public List<UnassignedFinding> Unassigned { get; set; } = new();

    /// <summary>
    /// Null when the answer can be used: one to six named themes, each with at least one real finding, and every
    /// finding in a theme or unassigned with a reason. Mistakes in the extras (an unknown finding id, a tension that
    /// names a finding outside its theme or stays within one source) do not fail the answer; <see cref="Tidy"/>
    /// removes them in code and says so (seen in a live run: one bad tension failed the whole synthesis).
    /// </summary>
    public static string? Problem(ModelSynthesis a, IReadOnlyDictionary<string, string> findingSources)
    {
        if (a.Themes.Count is 0 or > MultivocalSynthesiser.MaxThemesPerQuestion) return $"give one to {MultivocalSynthesiser.MaxThemesPerQuestion} themes.";
        foreach (var theme in a.Themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Name)) return "every theme needs a name.";
            if (!theme.Findings.Any(f => findingSources.ContainsKey(f.Trim()))) return $"theme \"{theme.Name}\" has none of the findings.";
        }
        var placed = a.Themes.SelectMany(t => t.Findings.Select(f => f.Trim())).Concat(a.Unassigned.Select(u => u.Finding.Trim())).ToHashSet();
        var missing = findingSources.Keys.Where(id => !placed.Contains(id)).ToList();
        if (missing.Count > 0) return $"findings {string.Join(", ", missing.Take(10))} are in no theme; put each in a theme or list it as unassigned with the reason.";
        if (a.Unassigned.Any(u => string.IsNullOrWhiteSpace(u.Reason))) return "every unassigned finding needs a reason.";
        return null;
    }

    /// <summary>
    /// Removes what does not hold from an accepted answer, in place, and says what was removed: finding ids that do not
    /// exist, and tensions that name a finding outside their theme or set two findings of one source against each other.
    /// </summary>
    public static List<string> Tidy(ModelSynthesis a, IReadOnlyDictionary<string, string> findingSources)
    {
        var notes = new List<string>();
        foreach (var theme in a.Themes)
        {
            var findings = theme.Findings.Select(f => f.Trim()).Distinct().ToList();
            var unknown = findings.Where(f => !findingSources.ContainsKey(f)).ToList();
            if (unknown.Count > 0) notes.Add($"\"{theme.Name}\" named {string.Join(", ", unknown)}, which are not findings; left out.");
            theme.Findings = findings.Where(findingSources.ContainsKey).ToList();

            var kept = new List<SynthesisTension>();
            foreach (var t in theme.Tensions)
            {
                string fa = t.FindingA.Trim(), fb = t.FindingB.Trim();
                if (!theme.Findings.Contains(fa) || !theme.Findings.Contains(fb))
                    notes.Add($"a tension in \"{theme.Name}\" ({fa}, {fb}) named a finding outside the theme; left out.");
                else if (findingSources[fa] == findingSources[fb])
                    notes.Add($"a tension in \"{theme.Name}\" ({fa}, {fb}) was within one source; left out.");
                else
                    kept.Add(new SynthesisTension { FindingA = fa, FindingB = fb, Description = t.Description.Trim() });
            }
            theme.Tensions = kept;
        }
        return notes;
    }
}

/// <summary>One theme as the model gave it.</summary>
public sealed class ModelTheme
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Findings { get; set; } = new();
    public List<SynthesisTension> Tensions { get; set; } = new();
}

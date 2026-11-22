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
/// quality, grey or formal) is counted in code, never by the model, so each theme says whether it rests on grey
/// literature, formal literature or both (block I-5). The run goes from "Extracted" to "Synthesised".
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
            bool formal = kind == MultivocalSearcher.FormalKind;
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
                        // A formal source has no outlet tier or Table 7 points (it is appraised with MMAT), and its quote
                        // was checked against its full text or abstract (decision 8).
                        Literature = formal ? "formal" : "grey",
                        Tier = formal ? 0 : TierOf(kind),
                        QualityPoints = formal ? 0 : points.GetValueOrDefault(source.Address),
                        EvidenceBasis = formal ? source.TextBasis ?? "abstract only" : "page text",
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
        // Table 7 points are for grey sources only; a formal source is appraised with MMAT and has none.
        var grey = sources.Where(s => s.Literature == "grey").ToList();
        theme.MeanQuality = grey.Count == 0 ? 0 : Math.Round(grey.Average(s => s.QualityPoints), 1);
        theme.EvidenceLabel = theme.FormalSources > 0 && theme.GreySources > 0 ? "grey and formal literature"
            : theme.FormalSources > 0 ? "formal literature only"
            : theme.Sources > 0 && theme.ThirdTier == theme.Sources ? "3rd-tier grey literature only"
            : "grey literature only";
        return theme;
    }
}

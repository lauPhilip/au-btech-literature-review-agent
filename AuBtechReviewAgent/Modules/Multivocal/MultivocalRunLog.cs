using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The call log and the metrics of a multivocal run (MLR G-8). The call log is llm-calls.json in the run folder, in
/// the systematic review's shape ({ RunSettings, Calls }). A run can be started more than once (a step that stops is
/// started again), so the calls of each start are added after the calls already in the file and numbered on from
/// them. The metrics are run-metrics.json, in the systematic review's shape so the metrics store can hold both kinds
/// of run, with the setting Module = "multivocal".
/// </summary>
public static class MultivocalRunLog
{
    public const string CallsFile = "llm-calls.json";
    public const string MetricsFile = "run-metrics.json";
    public const string Module = "multivocal";

    public sealed class CallLog
    {
        public RunSettingsRecord? RunSettings { get; set; }
        public List<LlmCallRecord> Calls { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The run's call log, or null when it has none or it cannot be read.</summary>
    public static CallLog? LoadCalls(string folder)
    {
        string path = Path.Join(folder, CallsFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CallLog>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    /// <summary>The calls of earlier starts followed by this start's calls, numbered on from the earlier ones.</summary>
    public static List<LlmCallRecord> Merge(IReadOnlyList<LlmCallRecord> earlier, IReadOnlyList<LlmCallRecord> now)
    {
        var merged = earlier.Select(Copy).ToList();
        int next = merged.Count == 0 ? 1 : merged.Max(c => c.Sequence) + 1;
        foreach (var call in now.OrderBy(c => c.Sequence))
        {
            var copy = Copy(call);
            copy.Sequence = next++;
            merged.Add(copy);
        }
        return merged;
    }

    /// <summary>Writes the merged call log and its settings record; nothing when there are no calls at all.</summary>
    public static async Task<CallLog?> WriteCallsAsync(string folder, IReadOnlyList<LlmCallRecord> earlier, IReadOnlyList<LlmCallRecord> now, string model)
    {
        var calls = Merge(earlier, now);
        if (calls.Count == 0) return null;
        var log = new CallLog { RunSettings = RecordingChatCompletionService.Summarize(calls, model, RecordingChatCompletionService.AppVersion), Calls = calls };
        await SafeFile.WriteAllTextAsync(Path.Join(folder, CallsFile), JsonSerializer.Serialize(log, Json));
        return log;
    }

    /// <summary>
    /// The metrics of a checked run, from its own files. The figures mean the same as in the systematic review's,
    /// read for grey sources: Included is the sources kept for the synthesis (passed the quality check), full text is
    /// a source whose kept page was extracted, the coding kappa is the mean agreement of the second placements, and the
    /// duration runs from the search to the end of the check. Holds no query text and no paper content.
    /// </summary>
    public static RunMetrics BuildMetrics(MultivocalReportInput input, MultivocalPaperFile paper, MultivocalCitationAudit? audit, IReadOnlyList<LlmCallRecord> calls)
    {
        var plan = input.Planned.Plan;
        var f = MultivocalPaperParts.Flow(input);
        var settings = new Dictionary<string, string>
        {
            ["Module"] = Module,
            ["ReviewKind"] = plan.ReviewKind,
            ["GreySearches"] = string.Join(",", plan.GreySearches.OrderBy(k => k, StringComparer.Ordinal)),
            ["QualityThreshold"] = input.Quality.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var m = new RunMetrics
        {
            RunId = input.Planned.RunId.ToString("N"),
            CompletedUtc = (paper.CheckedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            AppVersion = RecordingChatCompletionService.AppVersion,
            Model = paper.Model,
            QueryHash = RunMetrics.Sha(string.Join("|", plan.Numbered().Select(q => q.Question.Text))),
            Settings = settings,
        };
        m.ConfigFingerprint = RunMetrics.Sha(string.Join("|", new[] { m.Model, m.AppVersion }.Concat(settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))));

        m.Identified = f.Found;
        m.DuplicatesRemoved = f.Duplicates;
        m.Screened = f.Screened;
        m.Included = f.Passed;
        m.ScreeningKappa = input.Screening.Kappa;
        m.FullTextRetrieved = f.Extracted;
        m.FullTextShare = f.Passed == 0 ? 0 : Math.Round((double)f.Extracted / f.Passed, 4);

        m.SourcesBySearch = input.Ledger.Searches.Where(x => x.Error == null).GroupBy(x => x.Source)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(x => x.New));
        var scored = input.Quality.Sources.Where(x => x.Outcome is "Passed" or "Below threshold").ToList();
        m.MeanQualityPoints = scored.Count == 0 ? null : Math.Round(scored.Average(x => x.Points), 1);
        m.MaxQualityPoints = input.Quality.MaxPoints;

        var synthesis = input.Synthesis;
        m.KeyFindings = synthesis.Findings.Count;
        int judged = synthesis.Findings.Count + synthesis.Unsupported.Count;
        m.VerifiedFindingShare = judged == 0 ? 0 : Math.Round((double)synthesis.Findings.Count / judged, 4);
        m.ExtractionErrors = f.NotExtracted;
        m.ValuesRejected = synthesis.Unsupported.Count;
        m.ThematicSynthesisUsed = synthesis.Themes.Count > 0;
        m.Themes = synthesis.Themes.Count;
        m.StudiesCoded = synthesis.Findings.Select(x => x.Address).Distinct(StringComparer.Ordinal).Count();
        m.StudiesUncoded = Math.Max(0, f.Extracted - m.StudiesCoded);
        var kappas = synthesis.SecondPlacements.Where(p => p.Kappa.HasValue).Select(p => p.Kappa!.Value).ToList();
        m.CodingKappa = kappas.Count == 0 ? null : Math.Round(kappas.Average(), 3);

        if (audit != null)
        {
            m.StudiesCited = audit.SourcesCited;
            m.CoverageShare = audit.ReferenceListSize == 0 ? 0 : Math.Round((double)audit.SourcesCited / audit.ReferenceListSize, 4);
            var checks = audit.SupportChecks;
            m.CitationsFinal = VerdictCounts.From(checks.Select(c => c.Verdict));
            if (audit.InitialSupportSummary is { } initial)
                m.CitationsInitial = new VerdictCounts
                {
                    Total = initial.Checked, Supported = initial.Supported, PartiallySupported = initial.PartiallySupported,
                    NotSupported = initial.NotSupported, InsufficientEvidence = initial.InsufficientEvidence, Unverifiable = initial.Unverifiable,
                };
            m.CitationsBySection = checks.GroupBy(c => c.Field.StartsWith("synthesis", StringComparison.Ordinal) ? "synthesis" : c.Field)
                .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
            m.SecondChecked = checks.Count(c => c.SecondVerdict != null);
            m.SecondCheckAgreed = checks.Count(c => c.SecondVerdict != null && c.SecondVerdict == c.FirstVerdict);
            m.UpgradedBySecondCheck = checks.Count(c => c.ResolvedBy == "second check");
            m.Repaired = audit.Repairs.Count;
            m.InvalidMarkersStripped = audit.InvalidMarkersStripped;
        }

        var themeTexts = paper.Results.Where(r => r.Kind == "theme").Select(r => r.Text).ToList();
        m.SynthesisWords = themeTexts.Sum(Words);
        m.DiscussionWords = Words(paper.Discussion);
        var sentences = themeTexts.Append(paper.Discussion).SelectMany(CitationSupportChecker.SplitSentences).ToList();
        m.Sentences = sentences.Count;
        var refsPerSentence = sentences.Select(x => CitationSupportChecker.ReferencesIn(x).Count).ToList();
        int cited = refsPerSentence.Count(n => n > 0);
        m.CitedSentenceShare = sentences.Count == 0 ? 0 : Math.Round((double)cited / sentences.Count, 4);
        m.MultiPaperSentenceShare = cited == 0 ? 0 : Math.Round((double)refsPerSentence.Count(n => n > 1) / cited, 4);
        int words = m.SynthesisWords + m.DiscussionWords;
        m.CitationsPer100Words = words == 0 ? 0 : Math.Round(refsPerSentence.Sum() * 100.0 / words, 2);

        m.LlmCalls = calls.Count;
        m.LlmRetries = calls.Sum(c => Math.Max(0, c.Attempts - 1));
        m.LlmFailures = calls.Count(c => c.Outcome != "ok");
        m.Tokens = calls.Sum(c => RunMetrics.TokensIn(c.Usage));
        m.CostByStage = calls.GroupBy(c => c.Stage).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new StageCost
        {
            Calls = g.Count(),
            Seconds = Math.Round(g.Sum(c => c.DurationMs) / 1000.0, 1),
            Tokens = g.Sum(c => RunMetrics.TokensIn(c.Usage)),
        });
        if (paper.CheckedUtc is DateTime done && input.Ledger.SearchedUtc != default)
            m.DurationMinutes = Math.Round((done - input.Ledger.SearchedUtc).TotalMinutes, 1);
        return m;
    }

    private static int Words(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\S+").Count;

    private static LlmCallRecord Copy(LlmCallRecord c) => new()
    {
        Sequence = c.Sequence, Stage = c.Stage, StartedUtc = c.StartedUtc, DurationMs = c.DurationMs, ModelRequested = c.ModelRequested,
        ModelReported = c.ModelReported, Temperature = c.Temperature, Attempts = c.Attempts, Outcome = c.Outcome,
        PromptSha256 = c.PromptSha256, PromptChars = c.PromptChars, ResponseSha256 = c.ResponseSha256, ResponseChars = c.ResponseChars, Usage = c.Usage,
    };
}

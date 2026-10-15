using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Metrics" section of appsettings.json.</summary>
public class MetricsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder of the metrics store (relative paths are resolved against the content root).</summary>
    public string Folder { get; set; } = Path.Join("App_Data", "metrics");
}

/// <summary>Counts of citation verdicts.</summary>
public class VerdictCounts
{
    public int Total { get; set; }
    public int Supported { get; set; }
    public int PartiallySupported { get; set; }
    public int NotSupported { get; set; }
    public int InsufficientEvidence { get; set; }
    public int Unverifiable { get; set; }

    public double SupportedShare => Share(Supported);
    public double PartialShare => Share(PartiallySupported);
    public double NotSupportedShare => Share(NotSupported);
    public double InsufficientShare => Share(InsufficientEvidence);
    public double UnverifiableShare => Share(Unverifiable);
    private double Share(int n) => Total == 0 ? 0 : Math.Round((double)n / Total, 4);

    public static VerdictCounts From(IEnumerable<string> verdicts)
    {
        var list = verdicts.ToList();
        return new VerdictCounts
        {
            Total = list.Count,
            Supported = list.Count(v => v == CitationSupportChecker.Supported),
            PartiallySupported = list.Count(v => v == CitationSupportChecker.Partial),
            NotSupported = list.Count(v => v == CitationSupportChecker.NotSupported),
            InsufficientEvidence = list.Count(v => v == CitationSupportChecker.InsufficientEvidence),
            Unverifiable = list.Count(v => v == CitationSupportChecker.Unverifiable),
        };
    }
}

public class StageCost
{
    public int Calls { get; set; }
    public double Seconds { get; set; }
    public long Tokens { get; set; }
}

/// <summary>
/// Quality and cost figures of one completed run, computed in code from the run's own files. Written to
/// run-metrics.json in the run folder and appended to the metrics store, which outlives the run folder, so
/// runs can be compared across app versions and settings. Holds no query text and no paper content.
/// </summary>
public class RunMetrics
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public string CompletedUtc { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>Hash of the settings that shape the output (model, app version, synthesis options, run options).
    /// Runs with the same value were produced the same way, so they can be compared directly.</summary>
    public string ConfigFingerprint { get; set; } = "";
    public string QueryHash { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = new();

    // Search and screening
    public int Identified { get; set; }
    public int DuplicatesRemoved { get; set; }
    public int Screened { get; set; }
    public int Included { get; set; }
    public double? ScreeningKappa { get; set; }
    public int UncertainDecisions { get; set; }
    public int FullTextRetrieved { get; set; }
    public double FullTextShare { get; set; }

    // Extraction
    public int KeyFindings { get; set; }
    public double VerifiedFindingShare { get; set; }
    public int ExtractionErrors { get; set; }

    // Thematic synthesis
    public bool ThematicSynthesisUsed { get; set; }
    public string? FallbackReason { get; set; }
    public int Themes { get; set; }
    public int Codes { get; set; }
    public double AnchoredCodeShare { get; set; }
    public int StudiesCoded { get; set; }
    public int StudiesUncoded { get; set; }
    public double? CodingKappa { get; set; }

    // Coverage
    public int StudiesCited { get; set; }
    public double CoverageShare { get; set; }

    // Citations
    public VerdictCounts CitationsInitial { get; set; } = new();
    public VerdictCounts CitationsFinal { get; set; } = new();
    public Dictionary<string, VerdictCounts> CitationsBySection { get; set; } = new();
    public Dictionary<string, VerdictCounts> CitationsByEvidence { get; set; } = new();
    public Dictionary<string, VerdictCounts> CitationsByPapersInSentence { get; set; } = new();
    public int SecondChecked { get; set; }
    public int SecondCheckAgreed { get; set; }
    public int UpgradedBySecondCheck { get; set; }
    public int Repaired { get; set; }
    public int OutsideTheme { get; set; }
    public int InvalidMarkersStripped { get; set; }

    // Text
    public int SynthesisWords { get; set; }
    public int DiscussionWords { get; set; }
    public int Sentences { get; set; }
    public double CitedSentenceShare { get; set; }
    public double CitationsPer100Words { get; set; }
    public double MultiPaperSentenceShare { get; set; }

    // Grey literature (multivocal runs only; empty or null for a systematic review)
    /// <summary>New sources each grey search added, by search (after the ones before it in the run).</summary>
    public Dictionary<string, int> SourcesBySearch { get; set; } = new();

    /// <summary>The mean quality checklist points of the sources that were scored.</summary>
    public double? MeanQualityPoints { get; set; }

    /// <summary>The checklist's maximum points, for reading <see cref="MeanQualityPoints"/>.</summary>
    public double? MaxQualityPoints { get; set; }

    /// <summary>Extracted values the support check left out, because their quote did not say what the value claims.</summary>
    public int ValuesRejected { get; set; }

    // Cost
    public int LlmCalls { get; set; }
    public int LlmRetries { get; set; }
    public int LlmFailures { get; set; }
    public long Tokens { get; set; }
    public double DurationMinutes { get; set; }
    public Dictionary<string, StageCost> CostByStage { get; set; } = new();

    /// <summary>Whether the run is a multivocal one (setting Module = "multivocal"); systematic runs have no Module setting.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMultivocal => Settings.TryGetValue("Module", out var module) && module == "multivocal";

    /// <summary>App version without the build metadata, for display ("1.4.0+abc1234" → "1.4.0 (abc1234)").</summary>
    public string ShortVersion
    {
        get
        {
            int plus = AppVersion.IndexOf('+');
            if (plus < 0) return AppVersion;
            string meta = AppVersion[(plus + 1)..];
            return $"{AppVersion[..plus]} ({(meta.Length > 7 ? meta[..7] : meta)})";
        }
    }

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..12];

    private static int Words(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\S+").Count;

    /// <summary>Total tokens from the usage object a provider reported (Mistral and OpenAI-style names).</summary>
    public static long TokensIn(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(usageJson);
            long total = 0, parts = 0;
            void Walk(JsonElement e)
            {
                if (e.ValueKind != JsonValueKind.Object) return;
                foreach (var p in e.EnumerateObject())
                {
                    string name = p.Name.Replace("_", "").ToLowerInvariant();
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out long n))
                    {
                        if (name == "totaltokens") total = n;
                        else if (name is "prompttokens" or "completiontokens" or "inputtokencount" or "outputtokencount") parts += n;
                    }
                    else Walk(p.Value);
                }
            }
            Walk(doc.RootElement);
            return total > 0 ? total : parts;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>Builds the metrics of a completed run from its ledger, report, citation audit and call log.</summary>
    public static RunMetrics Build(Guid runId, ReviewState state, PrismaReport? report,
        IReadOnlyList<CitationSupportResult> checks, CitationSupportSummary? initial, int strippedMarkers,
        IReadOnlyList<LlmCallRecord> calls, IReadOnlyDictionary<string, string> settings)
    {
        var m = new RunMetrics
        {
            RunId = runId.ToString("N"),
            CompletedUtc = (state.CompletedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            AppVersion = state.RunSettings?.AppVersion ?? RecordingChatCompletionService.AppVersion,
            Model = state.RunSettings?.Model ?? "",
            QueryHash = Sha(state.SearchQuery ?? ""),
            Settings = settings.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        m.ConfigFingerprint = Sha(string.Join("|", new[] { m.Model, m.AppVersion }.Concat(settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))));

        var s = state.Stats;
        m.Identified = s.TotalIdentified;
        m.DuplicatesRemoved = s.DuplicatesRemoved;
        m.Screened = s.Screened;
        m.Included = s.Included;
        m.ScreeningKappa = s.ScreeningKappa;
        m.UncertainDecisions = s.UncertainDecisions;
        m.FullTextRetrieved = s.FullTextRetrieved;
        m.FullTextShare = s.Included == 0 ? 0 : Math.Round((double)s.FullTextRetrieved / s.Included, 4);

        var extractions = state.Extractions ?? new List<StudyExtraction>();
        var findings = extractions.Where(e => e.Error == null).SelectMany(e => e.KeyFindings)
            .Where(f => !f.Value.Equals("not reported", StringComparison.OrdinalIgnoreCase)).ToList();
        m.KeyFindings = findings.Count;
        m.VerifiedFindingShare = findings.Count == 0 ? 0 : Math.Round((double)findings.Count(f => f.QuoteVerified) / findings.Count, 4);
        m.ExtractionErrors = extractions.Count(e => e.Error != null);

        var book = state.ThematicSynthesis;
        m.ThematicSynthesisUsed = (report?.SynthesisSections?.Count ?? 0) > 0;
        m.FallbackReason = book?.FallbackReason;
        if (book != null)
        {
            m.Themes = m.ThematicSynthesisUsed ? book.Themes.Count : 0;
            m.Codes = book.Codes.Count;
            m.AnchoredCodeShare = book.Codes.Count == 0 ? 0 : Math.Round((double)book.Codes.Count(c => c.Anchor != "unanchored") / book.Codes.Count, 4);
            m.StudiesCoded = book.CodedStudies.Count();
            m.StudiesUncoded = book.Uncoded.Count;
            m.CodingKappa = book.SecondCoding?.Kappa;
        }

        int referenceCount = state.SynthesizedRecords.Count;
        m.StudiesCited = s.StudiesCitedInSynthesis;
        m.CoverageShare = referenceCount == 0 ? 0 : Math.Round((double)m.StudiesCited / referenceCount, 4);

        m.CitationsFinal = VerdictCounts.From(checks.Select(c => c.Verdict));
        if (initial != null)
        {
            m.CitationsInitial = new VerdictCounts
            {
                Total = initial.Checked, Supported = initial.Supported, PartiallySupported = initial.PartiallySupported,
                NotSupported = initial.NotSupported, InsufficientEvidence = initial.InsufficientEvidence, Unverifiable = initial.Unverifiable,
            };
        }
        m.CitationsBySection = checks.GroupBy(c => c.Field.StartsWith("synthesis", StringComparison.Ordinal) ? "synthesis" : c.Field == "discussionItem" ? "discussion" : c.Field)
            .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
        m.CitationsByEvidence = checks.GroupBy(c => string.IsNullOrEmpty(c.EvidenceBasis) ? "none" : c.EvidenceBasis)
            .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
        m.CitationsByPapersInSentence = checks.GroupBy(c => Math.Min(4, CitationSupportChecker.ReferencesIn(c.Sentence).Count) switch { >= 4 => "4+", var n => n.ToString() })
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
        m.SecondChecked = checks.Count(c => c.SecondVerdict != null);
        m.SecondCheckAgreed = checks.Count(c => c.SecondVerdict != null && c.SecondVerdict == c.FirstVerdict);
        m.UpgradedBySecondCheck = checks.Count(c => c.ResolvedBy == "second check");
        m.Repaired = s.CitationsRepaired;
        m.OutsideTheme = s.CitationsOutsideTheme;
        m.InvalidMarkersStripped = strippedMarkers;

        if (report != null)
        {
            string overview = Regex.Replace(report.SynthesisResultsItem ?? "", @"\[(MERMAID|TIKZ)_START\].*?\[(MERMAID|TIKZ)_END\]", "", RegexOptions.Singleline);
            var synthesisTexts = new[] { overview }.Concat((report.SynthesisSections ?? new()).Select(x => x.Text)).ToList();
            m.SynthesisWords = synthesisTexts.Sum(Words);
            m.DiscussionWords = Words(report.DiscussionItem);
            var sentences = synthesisTexts.Append(report.DiscussionItem ?? "").SelectMany(CitationSupportChecker.SplitSentences).ToList();
            m.Sentences = sentences.Count;
            var refsPerSentence = sentences.Select(x => CitationSupportChecker.ReferencesIn(x).Count).ToList();
            int cited = refsPerSentence.Count(n => n > 0);
            m.CitedSentenceShare = sentences.Count == 0 ? 0 : Math.Round((double)cited / sentences.Count, 4);
            m.MultiPaperSentenceShare = cited == 0 ? 0 : Math.Round((double)refsPerSentence.Count(n => n > 1) / cited, 4);
            int words = m.SynthesisWords + m.DiscussionWords;
            m.CitationsPer100Words = words == 0 ? 0 : Math.Round(refsPerSentence.Sum() * 100.0 / words, 2);
        }

        m.LlmCalls = calls.Count;
        m.LlmRetries = calls.Sum(c => Math.Max(0, c.Attempts - 1));
        m.LlmFailures = calls.Count(c => c.Outcome != "ok");
        m.Tokens = calls.Sum(c => TokensIn(c.Usage));
        m.CostByStage = calls.GroupBy(c => c.Stage).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new StageCost
        {
            Calls = g.Count(),
            Seconds = Math.Round(g.Sum(c => c.DurationMs) / 1000.0, 1),
            Tokens = g.Sum(c => TokensIn(c.Usage)),
        });
        if (state.CompletedUtc is DateTime done)
            m.DurationMinutes = Math.Round((done - state.Timestamp).TotalMinutes, 1);
        return m;
    }
}

/// <summary>
/// Append-only store of run metrics: one JSON object per line in runs.jsonl. It lives outside the run
/// folders, so it is not removed by the run clean-up, and it holds no query text or paper content.
/// </summary>
public class RunMetricsStore
{
    private readonly MetricsOptions _options;
    private readonly string _file;
    private static readonly object Lock = new();
    private static ILogger _log => AppLog.For<RunMetricsStore>();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public RunMetricsStore(MetricsOptions options, string contentRoot)
    {
        _options = options;
        string folder = Path.IsPathRooted(options.Folder) ? options.Folder : Path.Join(contentRoot, options.Folder);
        _file = Path.Join(folder, "runs.jsonl");
    }

    /// <summary>A store that keeps nothing (tests, tools).</summary>
    public static RunMetricsStore Disabled { get; } = new(new MetricsOptions { Enabled = false }, Path.GetTempPath());

    public bool Enabled => _options.Enabled;
    public string FilePath => _file;

    public void Append(RunMetrics metrics)
    {
        if (!_options.Enabled) return;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, JsonSerializer.Serialize(metrics, Json) + "\n");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run metrics not stored: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Every stored run of one kind, oldest first: the systematic runs, or with <paramref name="multivocal"/> the
    /// multivocal ones, so the figures of the two kinds of review are never mixed. A line that cannot be read is skipped.
    /// </summary>
    public List<RunMetrics> ReadAll(bool multivocal = false)
    {
        var list = new List<RunMetrics>();
        if (!_options.Enabled || !File.Exists(_file)) return list;
        int skipped = 0;
        string[] lines;
        lock (Lock) lines = File.ReadAllLines(_file);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<RunMetrics>(line) is { } m) list.Add(m); }
            catch (JsonException)
            {
                // A damaged line (for example from an interrupted write) is skipped so the rest stays readable.
                skipped++;
            }
        }
        if (skipped > 0) _log.LogWarning("Skipped {Count} unreadable line(s) in {File}", skipped, _file);
        return list.Where(m => m.IsMultivocal == multivocal).OrderBy(m => m.CompletedUtc, StringComparer.Ordinal).ToList();
    }
}

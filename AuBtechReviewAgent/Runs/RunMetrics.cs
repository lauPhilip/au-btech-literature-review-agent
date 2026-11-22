using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

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
public partial class RunMetrics
{
    /// <summary>2 since versions come from git tags (#105). Runs written with 1 were made before versioning: every
    /// build then called itself 1.0.0, so their version says nothing. Their files are kept as they are.</summary>
    public int SchemaVersion { get; set; } = 2;
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

    /// <summary>Whether the run was made before versions came from git tags, when every build said 1.0.0.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool BeforeVersioning => SchemaVersion < 2;

    /// <summary>The version for display: "v0.2.0 (40b00a0)", or "before versioning (40b00a0)" for an older run.</summary>
    public string ShortVersion => BeforeVersioning
        ? (ToolVersion.ShortCommit(AppVersion) is { } commit ? $"before versioning ({commit})" : "before versioning")
        : ToolVersion.Display(AppVersion);

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
}

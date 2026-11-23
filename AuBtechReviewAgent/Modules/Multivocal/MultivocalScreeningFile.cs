using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The screening of one found grey source.</summary>
public sealed class GreyScreening
{
    /// <summary>The source's address key (<see cref="MultivocalSearcher.AddressKey"/>), which links it to the ledger.</summary>
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>"Included", "Excluded", "Duplicate" (the same title found under another address) or "Error".</summary>
    public string Decision { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string? Summary { get; set; }
    public string? ExclusionReason { get; set; }
    public string? Confidence { get; set; }

    /// <summary>The two screenings disagreed, the confidence is low or the text looked like instructions: the reviewer should look.</summary>
    public bool Uncertain { get; set; }

    /// <summary>For a duplicate: the address of the source with the same title and producer that was screened.</summary>
    public string? DuplicateOf { get; set; }
    public ScreeningAnswer? First { get; set; }

    /// <summary>The second, independent screening; null for a source screened once (<see cref="MultivocalSecondScreening"/>).</summary>
    public ScreeningAnswer? Second { get; set; }

    /// <summary>Why the source was screened a second time: "included", "uncertain" or "flagged"; null when only the audit sample (or nothing) asked for it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondReason { get; set; }

    /// <summary>The source is in its pool's random audit sample, the only sources Cohen's κ is computed over.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AuditSample { get; set; }
    public List<string>? InjectionFlags { get; set; }
    public bool FromCache { get; set; }

    /// <summary>The model's decision, kept when the reviewer changes or confirms it.</summary>
    public string? ModelDecision { get; set; }

    /// <summary>The reviewer looked at this decision (and possibly changed it); <see cref="Decision"/> is then theirs.</summary>
    public bool Reviewed { get; set; }
    public string? ReviewerNote { get; set; }
    public DateTime? ReviewedUtc { get; set; }

    /// <summary>A source of the formal pool (an academic database's record); left out of a grey source's entry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Formal { get; set; }

    /// <summary>Still waiting for the reviewer: flagged and not yet looked at.</summary>
    public bool NeedsLook => Uncertain && !Reviewed;
}

/// <summary>The screening of a multivocal run's grey sources (multivocal-screening.json), which is in the run archive.</summary>
public sealed class MultivocalScreeningFile
{
    public Guid RunId { get; set; }
    public DateTime ScreenedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";

    /// <summary>
    /// The seed of the random audit sample (<see cref="MultivocalSecondScreening.SeedOf"/>); null in a run screened
    /// before 23 Nov 2026, when every source was screened twice.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuditSeed { get; set; }

    /// <summary>The share of each pool drawn for the audit sample, and its smallest size; null in an older run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AuditShare { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AuditMinimum { get; set; }

    /// <summary>
    /// Cohen's kappa between the two screenings, both pools together: over the random audit sample only, never over the
    /// targeted second screenings (in an older run, over every source).
    /// </summary>
    public double? Kappa { get; set; }

    /// <summary>With a formal pool: Cohen's kappa of the grey sources' audit sample; null, and left out, otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? KappaGrey { get; set; }

    /// <summary>With a formal pool: Cohen's kappa of the formal sources' audit sample; null, and left out, otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? KappaFormal { get; set; }

    /// <summary>With a formal pool: the version of the prompt for academic records; null, and left out, otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormalPromptVersion { get; set; }

    /// <summary>Warnings of the pilot check (<see cref="MultivocalPilotCheck"/>), one per pool; null, and left out, when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Warnings { get; set; }

    public List<GreyScreening> Sources { get; set; } = new();

    public int Count(string decision) => Sources.Count(s => s.Decision == decision);
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public ScreeningAnswer? Second { get; set; }
    public List<string>? InjectionFlags { get; set; }
    public bool FromCache { get; set; }

    /// <summary>The model's decision, kept when the reviewer changes or confirms it.</summary>
    public string? ModelDecision { get; set; }

    /// <summary>The reviewer looked at this decision (and possibly changed it); <see cref="Decision"/> is then theirs.</summary>
    public bool Reviewed { get; set; }
    public string? ReviewerNote { get; set; }
    public DateTime? ReviewedUtc { get; set; }

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

    /// <summary>Cohen's kappa between the two screenings, over the sources both screened.</summary>
    public double? Kappa { get; set; }
    public List<GreyScreening> Sources { get; set; } = new();

    public int Count(string decision) => Sources.Count(s => s.Decision == decision);
}

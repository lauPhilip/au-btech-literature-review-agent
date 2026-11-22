using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The answer to one checklist item for one source.</summary>
public sealed class QualityAnswer
{
    public string Id { get; set; } = "";

    /// <summary>1, 0.5 or 0.</summary>
    public double Score { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>The model's quote from the page, when it gave one.</summary>
    public string? Quote { get; set; }

    /// <summary>"code" (decided in code), "model" (the model's answer, its quote found in the page) or "lowered" (the model's quote was not in the page, so its points were not given).</summary>
    public string Basis { get; set; } = "";
}

/// <summary>The quality assessment of one included grey source.</summary>
public sealed class GreyQuality
{
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>The SHA-256 of the page text the answers were checked against.</summary>
    public string TextSha256 { get; set; } = "";
    public List<QualityAnswer> Answers { get; set; } = new();
    public double Points { get; set; }

    /// <summary>"Passed" (at or above the threshold), "Below threshold" or "Not assessed" (no readable page).</summary>
    public string Outcome { get; set; } = "";
    public string? NotAssessed { get; set; }
    public bool FromCache { get; set; }

    /// <summary>For an opinion piece: its main claim and the critical questions for an expert opinion (G13); null otherwise.</summary>
    public GreyArgument? Argument { get; set; }
}

/// <summary>One critical question about an opinion piece, and its answer.</summary>
public sealed class ArgumentAnswer
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string? Quote { get; set; }

    /// <summary>"model" (its quote found in the page), "unsupported" (the quote was not found), "reviewer" or "synthesis".</summary>
    public string Basis { get; set; } = "";
}

/// <summary>
/// The argument of an opinion piece (G13): the source's main claim P, as written, and the six critical questions for
/// an expert opinion that Garousi et al. take from Rainer's work. The model answers expertise, field, opinion and
/// backup evidence from the page; trustworthiness is left to the reviewer, as the paper notes it cannot be judged
/// reliably, and consistency with other experts is judged in the synthesis, where the sources are compared.
/// </summary>
public sealed class GreyArgument
{
    public string Claim { get; set; } = "";
    public bool ClaimFound { get; set; }
    public List<ArgumentAnswer> Questions { get; set; } = new();

    /// <summary>The six questions, in the paper's order; P is the claim and W its writer.</summary>
    public static readonly IReadOnlyList<(string Id, string Question)> CriticalQuestions = new[]
    {
        ("expertise", "How credible is the writer as an expert source?"),
        ("field", "Is the writer an expert in the field the claim belongs to?"),
        ("opinion", "What did the writer assert that implies the claim?"),
        ("trustworthiness", "Is the writer personally reliable as a source?"),
        ("consistency", "Does the claim agree with what other experts say?"),
        ("backup", "Is the writer's assertion based on evidence?"),
    };
}

/// <summary>The quality assessment of a multivocal run (multivocal-quality.json), which is in the run archive.</summary>
public sealed class MultivocalQualityFile
{
    public Guid RunId { get; set; }
    public DateTime AssessedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public int Threshold { get; set; }
    public int MaxPoints { get; set; } = GreyQualityChecklist.MaxPoints;
    public string ImpactRule { get; set; } = GreyQualityChecklist.ImpactRule;
    public List<GreyQuality> Sources { get; set; } = new();

    public int Count(string outcome) => Sources.Count(s => s.Outcome == outcome);
}

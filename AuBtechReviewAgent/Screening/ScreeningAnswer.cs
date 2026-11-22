using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>The screening model's structured answer for one record.</summary>
public class ScreeningAnswer
{
    public string Decision { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string BriefSummary { get; set; } = "";
    /// <summary>high, medium or low (older fakes and models may leave it out).</summary>
    public string? Confidence { get; set; }
    /// <summary>Only for Excluded: one of ExclusionReasons.ModelKeys (optional, so older answers stay valid).</summary>
    public string? ExclusionReason { get; set; }

    public bool IsIncluded => Decision.Equals("Included", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the answer is usable; otherwise the problem, which is sent back to the model once.</summary>
    public static string? Validate(ScreeningAnswer a)
    {
        if (!LlmJson.OneOf(a.Decision, "Included", "Excluded")) return "decision must be exactly \"Included\" or \"Excluded\".";
        if (string.IsNullOrWhiteSpace(a.Reasoning)) return "reasoning must not be empty.";
        if (!string.IsNullOrWhiteSpace(a.Confidence) && !LlmJson.OneOf(a.Confidence, "high", "medium", "low"))
            return "confidence must be \"high\", \"medium\" or \"low\".";
        if (!a.IsIncluded && !string.IsNullOrWhiteSpace(a.ExclusionReason) && !ExclusionReasons.ModelKeys.Contains(a.ExclusionReason.Trim().ToLowerInvariant()))
            return $"exclusionReason must be one of {string.Join(", ", ExclusionReasons.ModelKeys.Select(k => $"\"{k}\""))}.";
        return null;
    }

    /// <summary>
    /// The problem with an answer for a record that has no abstract: its reasoning or summary describes an abstract
    /// anyway (issue #132: "The abstract suggests a simulation-based evaluation" for a record with none).
    /// </summary>
    public static string? ValidateWithoutAbstract(ScreeningAnswer a) =>
        Validate(a) ?? (ClaimsAnAbstract(a.Reasoning) || ClaimsAnAbstract(a.BriefSummary)
            ? "This record has no abstract, but your reasoning or summary refers to one. Base both on the title and venue only, and do not describe an abstract."
            : null);

    // "no abstract", "without an abstract", "the abstract is missing": saying that there is none is fine.
    private static readonly Regex NoAbstractPhrase = new(
        @"\b(?:no|without|missing|lacks?|lacking|absent|unavailable|empty)\W+(?:\w+\W+){0,2}?abstracts?\b" +
        @"|\babstracts?\W+(?:\w+\W+){0,2}?(?:missing|absent|unavailable|empty|not\s+(?:available|provided|given|present))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AbstractWord = new(@"\babstracts?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the text speaks of an abstract other than to say there is none.</summary>
    public static bool ClaimsAnAbstract(string? text) =>
        !string.IsNullOrWhiteSpace(text) && AbstractWord.IsMatch(NoAbstractPhrase.Replace(text, " "));

    public ScreeningAnswer Normalized() => new()
    {
        Decision = IsIncluded ? "Included" : "Excluded",
        Reasoning = Reasoning.Trim(),
        BriefSummary = string.IsNullOrWhiteSpace(BriefSummary) ? "N/A" : BriefSummary.Trim(),
        Confidence = string.IsNullOrWhiteSpace(Confidence) ? null : Confidence.Trim().ToLowerInvariant(),
        ExclusionReason = IsIncluded || string.IsNullOrWhiteSpace(ExclusionReason) ? null : ExclusionReason.Trim().ToLowerInvariant(),
    };
}

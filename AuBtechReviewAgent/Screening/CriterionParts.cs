using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>One part of the inclusion criterion, and whether the record shows it.</summary>
public sealed class CriterionCheck
{
    /// <summary>The part, copied word for word from the inclusion criterion.</summary>
    public string Part { get; set; } = "";
    /// <summary>yes, no or unclear.</summary>
    public string Met { get; set; } = "";
    /// <summary>For yes: words copied from the title or abstract that show the part.</summary>
    public string? Quote { get; set; }
    /// <summary>Set in code when the model's answer was changed, with the reason.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// Screening against each part of the inclusion criterion (issue #564). In run 289a0d3e the criterion asked for
/// "some form of evaluation", and two records were let in on a proposal and on an abstract that "suggests" an
/// evaluation. The screener now names each part of the criterion and says whether the record shows it, and code
/// keeps a record only when every part is shown with a quote found in its title or abstract.
/// </summary>
public static class CriterionParts
{
    public const string Yes = "yes", No = "no", Unclear = "unclear";

    /// <summary>The step added to the systematic screening prompt, for both screeners.</summary>
    public const string Procedure = """
        CRITERION PARTS:
        Split the inclusion thresholds into their separate requirements, for example "describe or evaluate an agent framework" and "with some form of evaluation". Copy each part word for word from the inclusion thresholds. For each part, say whether this record shows it:
        - "yes" only with a quote, copied word for word from the title or abstract, that shows the part;
        - "no" when the record shows that it does not meet the part;
        - "unclear" when the record does not say.
        A proposal, a design or a plan is not an evaluation, and a record that only "suggests" a part does not show it. Decide Included only when every part is "yes".
        """;

    /// <summary>The extra field of the systematic screening answer.</summary>
    public const string AnswerField = """
        Add to that JSON object the field "criteriaParts": [{"part":"words copied from the inclusion thresholds","met":"yes, no or unclear","quote":"words copied from the title or abstract, only when yes"}].
        """;

    /// <summary>
    /// The problem with the parts of an answer, or null. The parts are optional, so an older answer stays valid; when
    /// they are there, each must come from the criterion, and an Included record may not miss one.
    /// </summary>
    public static string? Problem(ScreeningAnswer answer, string inclusionCriteria)
    {
        if (answer.CriteriaParts is not { Count: > 0 } parts) return null;
        string criterion = Canonical(inclusionCriteria);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Part)) return "every entry of criteriaParts needs a part.";
            if (!LlmJson.OneOf(part.Met, Yes, No, Unclear)) return "met must be \"yes\", \"no\" or \"unclear\".";
            if (criterion.Length > 0 && !criterion.Contains(Canonical(part.Part), StringComparison.Ordinal))
                return $"The part \"{part.Part.Trim()}\" is not in the inclusion thresholds. Copy each part word for word from them.";
        }
        if (answer.IsIncluded && parts.Any(p => LlmJson.OneOf(p.Met, No)))
            return "A record that does not meet a part of the inclusion thresholds cannot be Included.";
        return null;
    }

    /// <summary>
    /// Checks each "yes" against the record and decides in code: a part counts as shown only with a quote found in
    /// the title or abstract, and a record kept by the model is excluded when a part is not shown. Every change is
    /// written into the part's note and the reasoning, so the decision on file says why.
    /// </summary>
    public static ScreeningAnswer Apply(ScreeningAnswer answer, AcademicPaper paper)
    {
        if (answer.CriteriaParts is not { Count: > 0 } parts) return answer;
        string record = $"{paper.Title}\n{paper.Abstract}";
        foreach (var part in parts)
        {
            part.Part = part.Part.Trim();
            part.Met = part.Met.Trim().ToLowerInvariant();
            part.Quote = string.IsNullOrWhiteSpace(part.Quote) ? null : part.Quote.Trim();
            if (part.Met == Yes && !CitationSupportChecker.QuoteOccursIn(part.Quote, record))
            {
                part.Met = Unclear;
                part.Note = part.Quote == null
                    ? "Marked unclear in code: no quote from the record was given."
                    : "Marked unclear in code: the quote was not found in the title or abstract.";
            }
        }
        var missed = parts.Where(p => p.Met != Yes).ToList();
        if (!answer.IsIncluded || missed.Count == 0) return answer;

        bool notMet = missed.Any(p => p.Met == No);
        string names = string.Join("; ", missed.Select(p => $"\"{p.Part}\""));
        answer.Decision = "Excluded";
        answer.ExclusionReason = notMet ? ExclusionReasons.InclusionNotMet : ExclusionReasons.TooLittleInformation;
        answer.Reasoning = $"(Excluded in code: the record does not show this part of the inclusion criterion with a quote from its title or abstract: {names}. The screener had decided Included.) {answer.Reasoning}";
        return answer;
    }

    private static string Canonical(string? text) =>
        Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}

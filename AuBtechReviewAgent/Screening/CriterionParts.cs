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
/// keeps a record only when every part is shown with a quote found in its title or abstract. Parts that are
/// alternatives of one requirement ("an agent framework or orchestration loop") need only one of them shown (#670).
/// </summary>
public static partial class CriterionParts
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

    /// <summary>
    /// The step that says alternatives are one requirement (issue #670). It follows <see cref="Procedure"/> in the
    /// systematic screening prompt; code applies the same rule in <see cref="Requirements"/>.
    /// </summary>
    public const string AlternativesRule = """
        Alternatives joined by "or" are one requirement, for example "an agent framework or orchestration loop": either alternative is enough. Keep them in one part, or give each alternative as its own part right after the other. An alternative may be "no" or "unclear" when another alternative of the same requirement is "yes"; that requirement is then shown.
        """;

    /// <summary>The extra field of the systematic screening answer.</summary>
    public const string AnswerField = """
        Add to that JSON object the field "criteriaParts": [{"part":"words copied from the inclusion thresholds","met":"yes, no or unclear","quote":"words copied from the title or abstract, only when yes"}].
        """;

    /// <summary>
    /// The problem with the parts of an answer, or null. The parts are optional, so an older answer stays valid; when
    /// they are there, each must come from the criterion, and an Included record may not miss a requirement: a "no" is
    /// allowed on an alternative when another alternative of the same requirement is "yes" (issue #670).
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
        if (answer.IsIncluded && Requirements(parts, inclusionCriteria).Any(g =>
                g.Any(p => LlmJson.OneOf(p.Met?.Trim(), No)) && !g.Any(p => LlmJson.OneOf(p.Met?.Trim(), Yes))))
            return "A record that does not meet a part of the inclusion thresholds cannot be Included.";
        return null;
    }

    /// <summary>
    /// Checks each "yes" against the record and decides in code: a part counts as shown only with a quote found in
    /// the title or abstract, and a record kept by the model is excluded when a part is not shown. Every change is
    /// written into the part's note and the reasoning, so the decision on file says why.
    /// </summary>
    public static ScreeningAnswer Apply(ScreeningAnswer answer, AcademicPaper paper, string inclusionCriteria = "")
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
        var groups = Requirements(parts, inclusionCriteria);
        foreach (var group in groups.Where(g => g.Count > 1))
            foreach (var part in group)
            {
                string others = string.Join(" or ", group.Where(p => p != part).Select(p => $"\"{p.Part}\""));
                string note = $"Checked as an alternative to {others}; one alternative is enough.";
                part.Note = part.Note == null ? note : $"{part.Note} {note}";
            }
        var unmet = groups.Where(g => !g.Any(p => p.Met == Yes)).ToList();
        if (!answer.IsIncluded || unmet.Count == 0) return answer;

        bool notMet = unmet.Any(g => g.All(p => p.Met == No));
        string names = string.Join("; ", unmet.Select(g => string.Join(" or ", g.Select(p => $"\"{p.Part}\""))));
        answer.Decision = "Excluded";
        answer.ExclusionReason = notMet ? ExclusionReasons.InclusionNotMet : ExclusionReasons.TooLittleInformation;
        answer.Reasoning = $"(Excluded in code: the record does not show this part of the inclusion criterion with a quote from its title or abstract: {names}. The screener had decided Included.) {answer.Reasoning}";
        return answer;
    }

    /// <summary>
    /// The parts grouped into requirements (issue #670). A record meets a requirement when it shows one of its parts.
    /// The rule is kept simple on purpose, with no Boolean parsing: a part that starts with "or" or "and/or" is an
    /// alternative to the part before it, and two parts that are the two sides of one "X or Y" (or "X and/or Y")
    /// phrase of the inclusion criterion are alternatives of each other. Every other part is a requirement of its own,
    /// and "and" never makes an alternative.
    /// </summary>
    public static List<List<CriterionCheck>> Requirements(IReadOnlyList<CriterionCheck> parts, string? inclusionCriteria)
    {
        string criterion = $" {Canonical(inclusionCriteria)} ";
        var groups = new List<List<CriterionCheck>>();
        var groupOf = new Dictionary<CriterionCheck, List<CriterionCheck>>();
        foreach (var part in parts)
        {
            string text = Canonical(part.Part);
            List<CriterionCheck>? joins = null;
            if (groups.Count > 0 && LeadingOr().IsMatch(part.Part ?? "")) joins = groups[^1];
            else if (text.Length > 0)
                joins = groupOf.Where(e => Sides(Canonical(e.Key.Part), text, criterion)).Select(e => e.Value).FirstOrDefault();
            if (joins == null) groups.Add(joins = new List<CriterionCheck>());
            joins.Add(part);
            groupOf[part] = joins;
        }
        return groups;
    }

    /// <summary>True when the criterion holds "a or b", "b or a", or the same with "and/or", as whole words.</summary>
    private static bool Sides(string a, string b, string criterion) =>
        a.Length > 0 && a != b && new[] { " or ", " and or " }.Any(joint =>
            criterion.Contains($" {a}{joint}{b} ", StringComparison.Ordinal) || criterion.Contains($" {b}{joint}{a} ", StringComparison.Ordinal));

    [GeneratedRegex(@"^\s*(and\s*/\s*or|or)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingOr();

    private static string Canonical(string? text) =>
        Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}

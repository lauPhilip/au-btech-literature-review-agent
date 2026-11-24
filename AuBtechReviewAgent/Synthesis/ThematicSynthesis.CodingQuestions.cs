using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The research questions and eligibility criteria in the coding prompt (issue #430). The coding saw only the objective,
/// so a code that bore on a research question could be dropped as "not bearing on the objective". Without research
/// questions every part is empty and the coding prompt is as before.
/// </summary>
public static partial class ThematicSynthesis
{
    /// <summary>The prompt parts for the research questions and criteria: the aim, the task's wording, the rules and the answer field.</summary>
    private static (string Aim, string BearsOn, string Rules, string Field) CodingQuestionPrompt(string objective, IReadOnlyList<string> questions, string criteria)
    {
        string aim = ReviewWriter.AimBlock(objective, questions);
        if (questions.Count == 0) return (aim, "", "", "");
        if (!string.IsNullOrWhiteSpace(criteria))
            aim += $"\nELIGIBILITY CRITERIA (every study below met them and was included):\n{criteria.Trim()}";
        var ids = questions.Select((_, i) => ReviewWriter.QuestionId(i)).ToList();
        string rules = $"""

            - Name the research questions each code bears on in "questions" (e.g. ["{ids[0]}"]), or [] when it bears on none. A code that bears on a research question bears on the objective: never leave it out.{(string.IsNullOrWhiteSpace(criteria) ? "" : " Never call a study off-topic: every study met the eligibility criteria above.")}
            """;
        return (aim, " or on any of the research questions", rules, ",\"questions\":[\"RQ1\"]");
    }

    /// <summary>A finding's research question, as shown to the coding model ("" when the run has none or the finding bears on none).</summary>
    private static string QuestionTag(EvidencedValue finding) =>
        finding.Question is { } q && q != StudyExtractor.NoQuestion ? $" [{q}]" : "";

    /// <summary>Why the questions named by the codes cannot be used, or null.</summary>
    private static string? CodeQuestionProblem(IEnumerable<(int Ref, List<string>? Questions)> codes, IReadOnlyList<string> questions)
    {
        if (questions.Count == 0) return null;
        var ids = questions.Select((_, i) => ReviewWriter.QuestionId(i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, named) in codes)
        {
            var unknown = (named ?? new()).Select(q => q.Trim()).Where(q => !ids.Contains(q)).ToList();
            if (unknown.Count > 0)
                return $"study {reference} names unknown research questions ({string.Join(", ", unknown)}); use only {string.Join(", ", ids)}, or [].";
        }
        return null;
    }

    /// <summary>The questions of a code in canonical form, in question order; null for a run without research questions.</summary>
    private static List<string>? CodeQuestions(List<string>? named, IReadOnlyList<string> questions)
    {
        if (questions.Count == 0) return null;
        var given = (named ?? new()).Select(q => q.Trim().ToUpperInvariant()).ToHashSet();
        return questions.Select((_, i) => ReviewWriter.QuestionId(i)).Where(given.Contains).ToList();
    }
}

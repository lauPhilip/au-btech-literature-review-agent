using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The MMAT answers as a risk-of-bias overview: per study a traffic light for each question, and per study category
/// how the included studies answered each question. MMAT's authors discourage a single overall score, so there is
/// none: the table shows how many questions were met next to the answers, never instead of them. Colour is never the
/// only signal; every cell also says yes, no or can't tell.
/// </summary>
public static class RiskOfBias
{
    public sealed record Light(string Answer, string Word, string Dot, string Text);

    /// <summary>The traffic light for one MMAT answer ("yes", "no", "cant_tell", or none).</summary>
    public static Light For(string? answer) => answer switch
    {
        "yes" => new("yes", "Yes", "bg-verdict-ok", "text-verdict-ok"),
        "no" => new("no", "No", "bg-verdict-bad", "text-verdict-bad"),
        null => new("", "–", "bg-gray-200", "text-gray-500"),
        _ => new("cant_tell", "Can't tell", "bg-verdict-partial", "text-verdict-partial"),
    };

    public sealed record QuestionSummary(string Id, string Criterion, int Yes, int No, int CantTell)
    {
        public int Total => Yes + No + CantTell;
    }

    public sealed record CategorySummary(string Category, int Studies, IReadOnlyList<QuestionSummary> Questions);

    /// <summary>Per study category in the appraisal table: how the studies answered each of its five questions.</summary>
    public static List<CategorySummary> Summarise(IEnumerable<StudyExtraction> extractions) =>
        extractions
            .Where(e => e.Error == null && e.AppraisalCategory != "not_empirical" && e.Appraisal.Count > 0)
            .GroupBy(e => e.AppraisalCategory)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategorySummary(g.Key, g.Count(),
                g.SelectMany(e => e.Appraisal)
                 .GroupBy(a => a.Id)
                 .OrderBy(q => q.Key, StringComparer.Ordinal)
                 .Select(q => new QuestionSummary(q.Key, q.First().Criterion,
                     q.Count(a => a.Answer == "yes"), q.Count(a => a.Answer == "no"), q.Count(a => a.Answer is not ("yes" or "no"))))
                 .ToList()))
            .ToList();
}

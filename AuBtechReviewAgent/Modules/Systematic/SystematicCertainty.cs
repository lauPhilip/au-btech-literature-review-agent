using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The systematic review's input to the certainty rating (<see cref="EvidenceCertainty"/>): each answer to a research
/// question with the studies it cites and the themes the codebook maps to its question, and each study's MMAT
/// appraisal and reading basis from the extraction. Computed from the run's files whenever the report is shown or
/// archived, so the ratings follow the reviewer's checked edits and older runs get them too.
/// </summary>
public static class SystematicCertainty
{
    /// <summary>The ratings of a run's answers, or null when the run has no answers to research questions.</summary>
    public static CertaintyFile? Assess(PrismaReport report, ThematicCodebook? book, IReadOnlyList<StudyExtraction>? extractions, CertaintyRule? rule = null)
    {
        if (report.ResearchAnswers is not { Count: > 0 } answers) return null;
        rule ??= CertaintyRule.Default;
        string unansweredText = ReviewWriter.UnansweredText(WritingProfile.Systematic);
        var questions = answers.Select(a =>
        {
            string id = ReviewWriter.QuestionId(a.Number - 1);
            var why = book?.Unanswered?.FirstOrDefault(u => string.Equals(u.Question.Trim(), id, StringComparison.OrdinalIgnoreCase));
            string? notRated = why != null ? $"No evidence bears on this question ({why.Reason.Trim().TrimEnd('.')}), so it was answered in code and there is nothing to rate."
                : a.Answer?.Trim() == unansweredText ? "No evidence bears on this question, so it was answered in code and there is nothing to rate." : null;
            var themes = (book?.Themes ?? new List<Theme>())
                .Where(t => t.Questions?.Contains(id, StringComparer.OrdinalIgnoreCase) == true)
                .Select(t => new CertaintyTheme(t.Id, t.Name, t.Studies.Distinct().OrderBy(r => r).ToList())).ToList();
            return new CertaintyQuestion(a.Number, id, a.Question, a.Answer ?? "", ThematicSynthesis.CitedIn(a.Answer).ToList(), themes, notRated,
                Contested(book, themes.Select(t => t.Id)));
        }).ToList();
        var sources = Sources(book, extractions ?? Array.Empty<StudyExtraction>(), rule);
        return new CertaintyFile
        {
            Method = EvidenceCertainty.Method(rule),
            Rule = rule,
            Answers = EvidenceCertainty.Assess(questions, sources, rule),
        };
    }

    // The second coding writes each disagreement as "Study 3, T1 (Name): first coding only" (ThematicSynthesis.CompareCodings).
    private static readonly Regex Disagreement = new(@"^Study (\d+), (\S+) \(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The studies whose place in one of the question's themes the two independent codings disagree on; null when the
    /// run has no usable second coding, so coherence is reported as not assessed.
    /// </summary>
    public static List<int>? Contested(ThematicCodebook? book, IEnumerable<string> themeIds)
    {
        if (book?.SecondCoding is not { Error: null, CellsCompared: > 0 } second) return null;
        var ids = themeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return second.Disagreements.Select(d => Disagreement.Match(d))
            .Where(m => m.Success && ids.Contains(m.Groups[2].Value))
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().OrderBy(r => r).ToList();
    }

    /// <summary>Each extracted study as the rule sees it.</summary>
    public static List<CertaintySource> Sources(ThematicCodebook? book, IEnumerable<StudyExtraction> extractions, CertaintyRule? rule = null)
    {
        rule ??= CertaintyRule.Default;
        return extractions.GroupBy(e => e.ReferenceNumber).Select(g => g.First()).Select(e =>
        {
            bool usable = e.Error == null;
            bool empirical = usable && !string.IsNullOrWhiteSpace(e.AppraisalCategory)
                && e.AppraisalCategory is not ("not_empirical" or StudyExtractor.NotAppraised);
            int no = e.Appraisal.Count(a => a.Answer == "no");
            bool fails = usable && e.Appraisal.Count > 0 && no > rule.FailShare * e.Appraisal.Count;
            var tags = book?.Codes.Where(c => c.Reference == e.ReferenceNumber && c.Questions != null).ToList();
            IReadOnlyCollection<string>? questions = tags is { Count: > 0 } ? tags.SelectMany(c => c.Questions!).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
            return new CertaintySource(e.ReferenceNumber, empirical, e.EvidenceBasis is "abstract only" or StudyExtractor.NoText, fails, questions);
        }).ToList();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>The text of the certainty rating, all written in code: the phrase after an answer, the sensitivity sentence and the method.</summary>
public static partial class EvidenceCertainty
{
    private static readonly string[] Small = { "no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    private static string Number(int n) => n >= 0 && n < Small.Length ? Small[n] : n.ToString(CultureInfo.InvariantCulture);
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Count(int n, CertaintyWords w) => $"{Number(n)} {(n == 1 ? w.Source : w.Sources)}";
    private static string Refs(IEnumerable<int> refs) => string.Join(", ", refs.Select(r => $"[{r}]"));
    // Compared as whole percentages, so no floating-point equality decides the word.
    private static string Percent(double share)
    {
        int percent = (int)Math.Round(share * 100, MidpointRounding.AwayFromZero);
        return percent == 50 ? "half" : $"{percent.ToString(CultureInfo.InvariantCulture)}%";
    }

    /// <summary>The phrase after an answer: the overall level, the number of sources and the concerns that lowered it.</summary>
    private static string Phrase(CertaintyLevel overall, int n, int notEmpirical, List<CertaintyDomain> domains, CertaintyWords words)
    {
        var parts = new List<string> { Count(n, words) };
        if (notEmpirical > 0) parts.Add($"{Number(notEmpirical)} not empirical");
        foreach (var d in domains.Where(d => d.Concern == Serious))
        {
            int k = d.Sources.Count;
            string? part = d.Domain switch
            {
                Limitations => $"{Number(k)} fail{(k == 1 ? "s" : "")} most {words.Appraisal} criteria",
                AbstractOnly => $"{Number(k)} read from the abstract only",
                Relevance => $"{Number(k)} with no code on the question",
                Coherence => $"{Number(k)} placed by one coding only",
                _ => null,
            };
            if (part != null) parts.Add(part);
        }
        return $"({Word(overall)} certainty: {string.Join(", ", parts)})";
    }

    /// <summary>One sentence on what the answer still rests on without each kind of source left out.</summary>
    private static string SensitivitySentence(List<SensitivityCheck> checks, int n, CertaintyWords words)
    {
        var abstractOnly = checks[0];
        var failing = checks[1];
        if (abstractOnly.Removed.Count == 0 && failing.Removed.Count == 0)
            return $"No {words.Source} behind the answer was read from the abstract only or fails more than half of its {words.Appraisal} criteria, so leaving such {words.Sources} out changes nothing.";
        string Part(SensitivityCheck c, string which)
        {
            int k = c.Removed.Count;
            string rest = c.Remaining.Count == 0 ? $"the answer would rest on no {words.Source}" : $"the answer still rests on {Count(c.Remaining.Count, words)}";
            string themes = c.ThemesWithout.Count == 0 ? "" : $", and {(c.ThemesWithout.Count == 1 ? "theme" : "themes")} {string.Join(" and ", c.ThemesWithout)} would keep none";
            return $"without the {Count(k, words)} {which} ({Refs(c.Removed)}), {rest}{themes}";
        }
        var parts = new List<string>();
        if (abstractOnly.Removed.Count > 0) parts.Add(Part(abstractOnly, "read from the abstract only"));
        if (failing.Removed.Count > 0)
            parts.Add(Part(failing, failing.Removed.Count == 1 ? $"that fails more than half of its {words.Appraisal} criteria" : $"that fail more than half of their {words.Appraisal} criteria"));
        if (abstractOnly.Removed.Count == 0) parts.Add($"none was read from the abstract only");
        else if (failing.Removed.Count == 0) parts.Add($"none fails more than half of its {words.Appraisal} criteria");
        return Cap(string.Join("; ", parts)) + ".";
    }

    /// <summary>
    /// The results in plain sentences: how many answers have each level, and which answers would no longer hold, resting
    /// on fewer sources than the rule asks for, once the sources read from the abstract only or failing their appraisal are left out.
    /// </summary>
    public static string Summary(IReadOnlyList<CertaintyAssessment> answers, CertaintyRule? rule = null, CertaintyWords? words = null)
    {
        rule ??= CertaintyRule.Default;
        words ??= CertaintyWords.Systematic;
        if (answers.Count == 0) return "";
        var levels = new[] { "high", "moderate", "low", "very low" }
            .Select(l => (Level: l, Count: answers.Count(a => a.Overall == l))).Where(x => x.Count > 0)
            .Select(x => $"{Number(x.Count)} {(x.Count == 1 ? "is" : "are")} of {x.Level} certainty").ToList();
        int notRated = answers.Count(a => a.Overall == null);
        if (notRated > 0) levels.Add($"{Number(notRated)} {(notRated == 1 ? "is" : "are")} not rated, because no evidence bears on {(notRated == 1 ? "it" : "them")}");
        string head = answers.Count == 1
            ? (answers[0].Overall is { } only ? $"The answer to the research question is of {only} certainty." : "The answer to the research question is not rated, because no evidence bears on it.")
            : $"Of the {Number(answers.Count)} answers to the research questions, {Join(levels)}.";
        var fragile = answers.Where(a => a.Overall != null && a.Sensitivity.Any(c => c.Removed.Count > 0 && c.Remaining.Count < rule.FewestSources))
            .Select(a => $"RQ{a.Number}").ToList();
        var rated = answers.Where(a => a.Overall != null).ToList();
        string tail = rated.Count == 0 ? ""
            : fragile.Count == 0
                ? $" No rated answer comes to rest on fewer than {Count(rule.FewestSources, words)} when the {words.Sources} read from the abstract only, or those that fail more than half of their {words.Appraisal} criteria, are left out."
                : $" The {(fragile.Count == 1 ? "answer" : "answers")} to {Join(fragile)} would rest on fewer than {Count(rule.FewestSources, words)} without the {words.Sources} read from the abstract only, or those that fail more than half of their {words.Appraisal} criteria, so {(fragile.Count == 1 ? "it does" : "they do")} not hold without them.";
        return head + tail;
    }

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>
    /// The method of the rating, written in code from the rule's thresholds (PRISMA 2020 item 15). It names GRADE-CERQual
    /// and GRADE as the inspiration and says plainly that it is an automated approximation.
    /// </summary>
    public static string Method(CertaintyRule? rule = null, CertaintyWords? words = null)
    {
        rule ??= CertaintyRule.Default;
        words ??= CertaintyWords.Systematic;
        string share = Percent(rule.ConcernShare);
        return $"The certainty of the evidence behind each answer to a research question was rated in code, with no model call, by a rule inspired by GRADE-CERQual (Lewin et al., 2018) and GRADE (Guyatt et al., 2008). " +
               "It is an automated approximation, not a formal GRADE or GRADE-CERQual assessment made by people. " +
               $"The evidence behind an answer is the {words.Sources} it cites and the {words.Sources} of the themes the codebook maps to its question. " +
               $"The rating starts at high when all of them are empirical, at moderate when at least {Percent(rule.ModerateStartShare)} are, and at low otherwise; {words.Appraisal} does not rank designs as GRADE ranks randomised trials above observational studies, so the start reflects only whether the {words.Sources} are empirical. " +
               $"It is lowered by {(rule.LevelsPerConcern == 1 ? "one level" : $"{rule.LevelsPerConcern} levels")}, to very low at most, for each serious concern: more than {share} of the {words.Sources} fail more than {Percent(rule.FailShare)} of their {words.Appraisal} criteria (methodological limitations; an answer of can't tell is not a failure, since it mostly means the abstract does not say); " +
               $"more than {share} were read from the abstract only; more than {share} were placed in a theme of the question by only one of two independent codings of their quotes (coherence), rated only where the run has a second coding; the answer rests on fewer than {rule.FewestSources} {words.Sources}, so a single {words.Source} is never enough (adequacy of data); and more than {share} have no code that bears on the question (relevance), rated only where the codes name their questions. " +
               $"A sensitivity check counts the {words.Sources} each answer still rests on without those read from the abstract only, and without those that fail more than {Percent(rule.FailShare)} of their {words.Appraisal} criteria, and names any theme of the question that would keep none. " +
               $"The thresholds and every rating, with its reasons, are in {CertaintyFile.FileName}.";
    }
}

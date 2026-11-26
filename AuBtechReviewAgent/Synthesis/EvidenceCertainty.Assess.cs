using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The certainty of the evidence behind each answer to a research question, rated in code with no model call. The
/// rule is inspired by GRADE-CERQual and GRADE but is an automated approximation, not a formal assessment made by
/// people: it starts at a level set by how many sources are empirical, then lowers it one level for each serious
/// concern (methodological limitations, reading from the abstract only, coherence, adequacy of data, relevance),
/// to very low at most. Every concern names its reason and its sources. A sensitivity check counts what an answer
/// still rests on without the sources read from the abstract only and without those that fail their appraisal.
/// </summary>
public static partial class EvidenceCertainty
{
    public const string Limitations = "Methodological limitations";
    public const string AbstractOnly = "Read from the abstract only";
    public const string Coherence = "Coherence";
    public const string Adequacy = "Adequacy of data";
    public const string Relevance = "Relevance";

    public const string NoConcern = "no serious concern";
    public const string Serious = "serious concern";
    public const string NotAssessed = "not assessed";

    /// <summary>The sources behind an answer: those it cites and those of the themes mapped to its question, in number order.</summary>
    public static List<int> SourcesBehind(CertaintyQuestion q) =>
        q.Cited.Concat(q.Themes.SelectMany(t => t.Sources)).Distinct().OrderBy(r => r).ToList();

    /// <summary>Every answer's rating, in question order.</summary>
    public static List<CertaintyAssessment> Assess(IEnumerable<CertaintyQuestion> questions, IReadOnlyCollection<CertaintySource> sources,
        CertaintyRule? rule = null, CertaintyWords? words = null) =>
        questions.OrderBy(q => q.Number).Select(q => Assess(q, sources, rule ?? CertaintyRule.Default, words ?? CertaintyWords.Systematic)).ToList();

    public static CertaintyAssessment Assess(CertaintyQuestion q, IReadOnlyCollection<CertaintySource> sources, CertaintyRule rule, CertaintyWords words)
    {
        var behind = SourcesBehind(q);
        string brief = AnswerInBrief(q.Answer);
        if (q.NotRated != null || behind.Count == 0)
        {
            string why = q.NotRated ?? $"The answer cites no {words.Source} and no theme is mapped to the question, so there is no evidence to rate.";
            return new CertaintyAssessment
            {
                Number = q.Number, Question = q.Question, AnswerInBrief = brief, NotRated = why,
                Phrase = "(certainty not rated: no evidence bears on this question)",
                SensitivitySentence = "There is no evidence behind the answer to leave out.",
            };
        }
        var byRef = sources.GroupBy(s => s.Reference).ToDictionary(g => g.Key, g => g.First());
        CertaintySource Of(int r) => byRef.TryGetValue(r, out var s) ? s : new CertaintySource(r, false, false, false, null);
        var studies = behind.Select(Of).ToList();
        int n = studies.Count;

        var notEmpirical = studies.Where(s => !s.Empirical).Select(s => s.Reference).ToList();
        int empirical = n - notEmpirical.Count;
        var start = notEmpirical.Count == 0 ? CertaintyLevel.High
            : empirical >= rule.ModerateStartShare * n ? CertaintyLevel.Moderate : CertaintyLevel.Low;
        string startReason = notEmpirical.Count == 0
            ? $"All {Count(n, words)} are empirical."
            : $"{Cap(Number(empirical))} of the {Count(n, words)} {(empirical == 1 ? "is" : "are")} empirical; {Refs(notEmpirical)} {(notEmpirical.Count == 1 ? "is" : "are")} not (position papers, system descriptions or reviews).";

        var domains = new List<CertaintyDomain>
        {
            ShareDomain(Limitations, studies.Where(s => s.FailsAppraisal).Select(s => s.Reference).ToList(), n, rule, words,
                k => $"fail{(k == 1 ? "s" : "")} more than half of {(k == 1 ? "its" : "their")} {words.Appraisal} criteria",
                $"No {words.Source} behind the answer fails more than half of its {words.Appraisal} criteria (an answer of can't tell is not a failure)."),
            ShareDomain(AbstractOnly, studies.Where(s => s.AbstractOnly).Select(s => s.Reference).ToList(), n, rule, words,
                k => $"{(k == 1 ? "was" : "were")} read from the abstract only", $"Every {words.Source} behind the answer was read in full text."),
            CoherenceDomain(q.Id, q.Contested, behind, n, rule, words),
            AdequacyDomain(behind, rule, words),
            RelevanceDomain(q.Id, studies, rule, words),
        };
        int concerns = domains.Count(d => d.Concern == Serious);
        var overall = (CertaintyLevel)Math.Max((int)CertaintyLevel.VeryLow, (int)start - concerns * rule.LevelsPerConcern);
        var sensitivity = new List<SensitivityCheck>
        {
            Without(AbstractOnly.ToLowerInvariant(), studies.Where(s => s.AbstractOnly).Select(s => s.Reference), behind, q.Themes),
            Without($"fails more than half of the {words.Appraisal} criteria", studies.Where(s => s.FailsAppraisal).Select(s => s.Reference), behind, q.Themes),
        };
        return new CertaintyAssessment
        {
            Number = q.Number, Question = q.Question, AnswerInBrief = brief, Sources = behind,
            Start = Word(start), StartReason = startReason, Domains = domains, Overall = Word(overall),
            Phrase = Phrase(overall, n, notEmpirical.Count, domains, words),
            Sensitivity = sensitivity, SensitivitySentence = SensitivitySentence(sensitivity, n, words),
        };
    }

    /// <summary>A domain that is a serious concern when more than the rule's share of the sources is affected.</summary>
    private static CertaintyDomain ShareDomain(string name, List<int> affected, int n, CertaintyRule rule, CertaintyWords words, Func<int, string> what, string none)
    {
        if (affected.Count == 0) return new CertaintyDomain { Domain = name, Concern = NoConcern, Reason = none };
        bool serious = affected.Count > rule.ConcernShare * n;
        string reason = $"{Cap(Number(affected.Count))} of the {Count(n, words)} ({Refs(affected)}) {what(affected.Count)}"
            + (serious ? $", more than {Percent(rule.ConcernShare)} of them." : $", not more than {Percent(rule.ConcernShare)} of them.");
        return new CertaintyDomain { Domain = name, Concern = serious ? Serious : NoConcern, Reason = reason, Sources = affected };
    }

    /// <summary>
    /// Coherence from the second, independent coding of the quotes: a source whose place in a theme of this question the
    /// two codings disagree on does not fit the answer's evidence cleanly.
    /// </summary>
    private static CertaintyDomain CoherenceDomain(string id, IReadOnlyList<int>? contested, List<int> behind, int n, CertaintyRule rule, CertaintyWords words)
    {
        if (contested == null)
            return new CertaintyDomain { Domain = Coherence, Concern = NotAssessed, Reason = $"The run has no second, independent coding of the {words.Sources}' quotes, so coherence is not rated and lowers nothing." };
        var affected = contested.Where(behind.Contains).Distinct().OrderBy(r => r).ToList();
        return ShareDomain(Coherence, affected, n, rule, words,
            k => $"{(k == 1 ? "was" : "were")} placed in a theme of {id} by only one of two independent codings of {(k == 1 ? "its" : "their")} quotes",
            $"Two independent codings of the quotes agree on where every {words.Source} behind the answer belongs among the themes of {id}.");
    }

    private static CertaintyDomain AdequacyDomain(List<int> behind, CertaintyRule rule, CertaintyWords words)
    {
        bool serious = behind.Count < rule.FewestSources;
        string reason = serious
            ? $"The answer rests on {Count(behind.Count, words)} ({Refs(behind)}), fewer than {rule.FewestSources}."
            : $"The answer rests on {Count(behind.Count, words)}.";
        return new CertaintyDomain { Domain = Adequacy, Concern = serious ? Serious : NoConcern, Reason = reason, Sources = serious ? behind : new() };
    }

    /// <summary>Relevance from the questions the codes name: a source none of whose codes bears on this question is indirect evidence for it.</summary>
    private static CertaintyDomain RelevanceDomain(string id, List<CertaintySource> studies, CertaintyRule rule, CertaintyWords words)
    {
        var tagged = studies.Where(s => s.Questions != null).ToList();
        if (tagged.Count == 0)
            return new CertaintyDomain { Domain = Relevance, Concern = NotAssessed, Reason = "No code names the question it bears on, so relevance is not rated and lowers nothing." };
        var indirect = tagged.Where(s => !s.Questions!.Contains(id, StringComparer.OrdinalIgnoreCase)).Select(s => s.Reference).ToList();
        return ShareDomain(Relevance, indirect, studies.Count, rule, words,
            k => $"{(k == 1 ? "has" : "have")} no code that bears on {id}", $"Every {words.Source} behind the answer has a code that bears on {id}.");
    }

    private static SensitivityCheck Without(string leavesOut, IEnumerable<int> removed, List<int> behind, IReadOnlyList<CertaintyTheme> themes)
    {
        var gone = removed.Where(behind.Contains).Distinct().OrderBy(r => r).ToList();
        return new SensitivityCheck
        {
            LeavesOut = leavesOut,
            Removed = gone,
            Remaining = behind.Except(gone).ToList(),
            ThemesWithout = gone.Count == 0 ? new() : themes.Where(t => t.Sources.Count > 0 && t.Sources.All(gone.Contains)).Select(t => $"{t.Id} ({t.Name})").ToList(),
        };
    }

    /// <summary>The answer's first sentence, for the table; at most 25 words.</summary>
    public static string AnswerInBrief(string answer)
    {
        string first = CitationSupportChecker.SplitSentences(answer ?? "").FirstOrDefault()?.Trim() ?? "";
        var w = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return w.Length <= 25 ? first : string.Join(" ", w.Take(25)).TrimEnd(',', ';', ':') + " …";
    }

    public static string Word(CertaintyLevel level) => level switch
    {
        CertaintyLevel.High => "high", CertaintyLevel.Moderate => "moderate", CertaintyLevel.Low => "low", _ => "very low",
    };
}

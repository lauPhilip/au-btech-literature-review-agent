using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The codebook of the thematic synthesis: themes built from the codes, and the second, independent coding.</summary>
public static partial class ThematicSynthesis
{
    private sealed class CodebookAnswer { public List<ThemeAnswer>? Themes { get; set; } public List<UnassignedAnswer>? Unassigned { get; set; } public List<UnansweredAnswer>? Unanswered { get; set; } }
    private sealed class ThemeAnswer { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public List<string>? Codes { get; set; } public List<string>? Questions { get; set; } public string? SingleStudyReason { get; set; } }
    private sealed class UnassignedAnswer { public string Code { get; set; } = ""; public string Reason { get; set; } = ""; }
    private sealed class UnansweredAnswer { public string Question { get; set; } = ""; public string Reason { get; set; } = ""; }

    /// <summary>
    /// Groups the codes into descriptive themes. The model must place every coded study in at least one theme
    /// (through one of its codes) or say why its codes fit none; the answer is rejected otherwise. With research
    /// questions, every theme names the questions it answers and every question is answered by a theme or listed as
    /// unanswered with a reason (filled into <paramref name="unanswered"/>); the themes are put in question order.
    /// </summary>
    public static async Task<(List<Theme> Themes, List<UncodedStudy> Unplaced)> BuildCodebookAsync(
        IChatCompletionService chat, string objective, IReadOnlyList<ThematicCode> codes, int maxThemes, string criteria = "",
        IReadOnlyList<string>? questions = null, List<UnansweredQuestion>? unanswered = null, SynthesisCache? cache = null, SynthesisReuse? reuse = null)
    {
        questions ??= Array.Empty<string>();
        var questionIds = questions.Select((_, i) => ReviewWriter.QuestionId(i)).ToList();
        var studies = codes.Select(c => c.Reference).Distinct().ToList();
        var (min, max) = ThemeRange(studies.Count, maxThemes);
        var ids = codes.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A code's research questions are shown when the coding named them, so the themes can be organised by question.
        string codeList = string.Join("\n", codes.Select(c => $"{c.Id} [study {c.Reference}]{(c.Questions is { Count: > 0 } q ? $" ({string.Join(", ", q)})" : "")}: {c.Label}"));

        // Every study here was included, so a reason to leave it out may not contradict the criteria it met (issue #166).
        string criteriaBlock = string.IsNullOrWhiteSpace(criteria) ? "" : $"\nELIGIBILITY CRITERIA (every study below met them and was included):\n{criteria.Trim()}\n";
        string criteriaRule = string.IsNullOrWhiteSpace(criteria) ? "" : " Never leave a code out because its study is outside the topic, population, setting or intervention: every study met the eligibility criteria above, and a reason must not contradict them.";
        // With research questions the themes are organised by question (the real runs grouped them by outcome numbers).
        string questionRule = questions.Count == 0 ? "" : $"""

            - Organise the themes by research question: every theme answers one or more of the research questions above, named in "questions" (e.g. ["RQ1"]). Every research question is answered by at least one theme, or, when no code bears on it, listed under "unanswered" with a one-sentence reason; never stretch a theme to cover a question its codes do not answer.
            """;
        string questionField = questions.Count == 0 ? "" : ",\"questions\":[\"RQ1\"]";
        string unansweredField = questions.Count == 0 ? "" : ",\"unanswered\":[{\"question\":\"RQ2\",\"reason\":\"...\"}]";
        string prompt = $$"""
            You are building the codebook for the thematic synthesis of a systematic literature review: group the codes below into descriptive themes.

            {{ReviewWriter.AimBlock(objective, questions)}}{{criteriaBlock}}
            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(codeList, "codes, each with the study it came from")}}

            TASK: Group the codes into {{min}} to {{max}} themes. A theme names a pattern that several studies share and that answers part of the objective; it is not a single study. A theme needs codes from at least two studies; a theme whose codes all come from one study is allowed only when that study answers a part of the objective no other study touches, and then it gives that reason in one sentence in the theme's "singleStudyReason" field. Give each theme a short name (at most 8 words) and a one- or two-sentence description of what unites its codes.
            - Put every code in the theme it fits best; a code may also go into a second theme if it clearly belongs to both.
            - Every study should end up in at least one theme. Only leave a code out if it does not bear on the objective; list it under "unassigned" with a reason.{{criteriaRule}}
            - Use only the code ids listed above.{{questionRule}}
            Respond ONLY with a minified JSON object:
            {"themes":[{"name":"...","description":"...","codes":["C1","C4"]{{questionField}}}],"unassigned":[{"code":"C9","reason":"..."}]{{unansweredField}}}
            """;

        // A reused codebook passes the same check again: known code ids only, every coded study placed or left out with a reason.
        string? Validate(CodebookAnswer a)
        {
            if (a.Themes == null || a.Themes.Count == 0) return "themes must be a non-empty list.";
            if (a.Themes.Count > max) return $"give at most {max} themes.";
            if (a.Themes.Any(t => string.IsNullOrWhiteSpace(t.Name) || t.Codes == null || t.Codes.Count == 0)) return "every theme needs a name and at least one code.";
            var unknown = a.Themes.SelectMany(t => t.Codes!).Concat((a.Unassigned ?? new()).Select(u => u.Code)).Where(c => !ids.Contains(c.Trim())).Distinct().ToList();
            if (unknown.Count > 0) return $"unknown code ids: {string.Join(", ", unknown.Take(10))}.";
            var unplaced = StudiesWithoutTheme(codes, a.Themes.Select(t => t.Codes!), (a.Unassigned ?? new()).Select(u => u.Code));
            if (unplaced.Count > 0)
                return $"studies {string.Join(", ", unplaced)} are in no theme and none of their codes is listed as unassigned. Place one of their codes in the best fitting theme, or list the codes under unassigned with a reason.";
            if (SingleStudyThemeProblem(a.Themes.Select(t => (t.Name, t.Codes!.AsEnumerable(), t.SingleStudyReason)), codes, studies.Count) is { } single) return single;
            return questions.Count == 0 ? null : QuestionProblem(questionIds, a.Themes.Select(t => (IEnumerable<string>)(t.Questions ?? new())), (a.Unanswered ?? new()).Select(u => u.Question));
        }
        var (answer, fromCache) = await CachedAnswerAsync<CodebookAnswer>(chat, prompt, PrismaReviewEngine.JsonMode(0.1), Validate, cache, CodebookCacheKind, CodebookPromptVersion);
        if (reuse != null) reuse.CodebookReused = fromCache;

        var byId = codes.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var themes = answer.Themes!.Select(t =>
        {
            var codeIds = t.Codes!.Select(c => c.Trim().ToUpperInvariant()).Where(byId.ContainsKey).Distinct().ToList();
            return new Theme
            {
                Name = t.Name.Trim().TrimEnd('.'),
                Description = t.Description.Trim(),
                CodeIds = codeIds,
                Studies = codeIds.Select(c => byId[c].Reference).Distinct().OrderBy(r => r).ToList(),
                SingleStudyReason = codeIds.Select(c => byId[c].Reference).Distinct().Count() == 1 && !string.IsNullOrWhiteSpace(t.SingleStudyReason) ? t.SingleStudyReason.Trim() : null,
                Questions = questions.Count == 0 ? null
                    : questionIds.Where(q => (t.Questions ?? new()).Any(x => x.Trim().Equals(q, StringComparison.OrdinalIgnoreCase))).ToList(),
            };
        }).Where(t => t.Studies.Count > 0)
            // In question order (a stable sort), so the results read question by question; without questions the order is the model's.
            .OrderBy(t => t.Questions is { Count: > 0 } q ? questionIds.IndexOf(q[0]) : 0).ToList();
        for (int i = 0; i < themes.Count; i++) themes[i].Id = $"T{i + 1}";
        if (questions.Count > 0 && unanswered != null)
        {
            var answered = themes.SelectMany(t => t.Questions!).ToHashSet();
            var why = (answer.Unanswered ?? new()).GroupBy(u => u.Question.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First().Reason.Trim());
            // A question whose only theme had no studies left is unanswered too, said in code.
            unanswered.AddRange(questionIds.Where(q => !answered.Contains(q)).Select(q =>
                new UnansweredQuestion(q, why.TryGetValue(q, out var w) && w.Length > 0 ? w : "No theme of the codebook answers it.")));
        }

        var placed = themes.SelectMany(t => t.Studies).ToHashSet();
        var reasons = (answer.Unassigned ?? new()).Where(u => byId.ContainsKey(u.Code.Trim()))
            .GroupBy(u => byId[u.Code.Trim()].Reference).ToDictionary(g => g.Key, g => g.First().Reason);
        var unplacedStudies = studies.Where(s => !placed.Contains(s)).OrderBy(s => s)
            .Select(s => new UncodedStudy(s, reasons.TryGetValue(s, out var r) && !string.IsNullOrWhiteSpace(r) ? $"Codes left out of the themes: {r.Trim()}" : "Codes left out of the themes."))
            .ToList();
        return (themes, unplacedStudies);
    }

    /// <summary>
    /// Why the questions of a codebook answer cannot be used, or null: a theme without a question or with an unknown one,
    /// and a question that is in neither a theme nor the unanswered list (or in both). Public for unit tests.
    /// </summary>
    public static string? QuestionProblem(IReadOnlyList<string> questionIds, IEnumerable<IEnumerable<string>> themeQuestions, IEnumerable<string> unanswered)
    {
        var perTheme = themeQuestions.Select(q => q.Select(x => x.Trim().ToUpperInvariant()).ToList()).ToList();
        var left = unanswered.Select(x => x.Trim().ToUpperInvariant()).ToList();
        var known = questionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = perTheme.SelectMany(q => q).Concat(left).Where(q => !known.Contains(q)).Distinct().ToList();
        if (unknown.Count > 0) return $"unknown research questions: {string.Join(", ", unknown)}; use only {string.Join(", ", questionIds)}.";
        if (perTheme.Any(q => q.Count == 0)) return "every theme names the research questions it answers in \"questions\".";
        var answered = perTheme.SelectMany(q => q).ToHashSet();
        var both = left.Where(answered.Contains).Distinct().ToList();
        if (both.Count > 0) return $"{string.Join(", ", both)} is answered by a theme and also listed as unanswered; keep it in one place.";
        var missing = questionIds.Where(q => !answered.Contains(q) && !left.Contains(q)).ToList();
        return missing.Count == 0 ? null
            : $"{string.Join(", ", missing)} is in no theme and not listed as unanswered. Name it in the \"questions\" of the themes that answer it, or list it under \"unanswered\" with a reason.";
    }

    /// <summary>Coded studies that are in no theme and have none of their codes listed as unassigned.</summary>
    public static List<int> StudiesWithoutTheme(IReadOnlyList<ThematicCode> codes, IEnumerable<IEnumerable<string>> themeCodes, IEnumerable<string> unassigned)
    {
        var assigned = themeCodes.SelectMany(c => c).Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = unassigned.Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return codes.GroupBy(c => c.Reference)
            .Where(g => !g.Any(c => assigned.Contains(c.Id)) && !g.Any(c => left.Contains(c.Id)))
            .Select(g => g.Key).OrderBy(r => r).ToList();
    }

    /// <summary>
    /// Why a theme rests on one study without a reason, or null (issue #447): a theme is a pattern several studies share,
    /// and a one-study theme asked the peer review for comparisons it could not make. With fewer than two coded studies
    /// every theme rests on one, and no reason is asked for. Public for unit tests.
    /// </summary>
    public static string? SingleStudyThemeProblem(IEnumerable<(string Name, IEnumerable<string> Codes, string? Reason)> themes, IReadOnlyList<ThematicCode> codes, int codedStudies)
    {
        if (codedStudies < 2) return null;
        var studyOf = codes.ToDictionary(c => c.Id, c => c.Reference, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, themeCodes, reason) in themes)
        {
            var studies = themeCodes.Select(c => c.Trim()).Where(studyOf.ContainsKey).Select(c => studyOf[c]).Distinct().ToList();
            if (studies.Count == 1 && string.IsNullOrWhiteSpace(reason))
                return $"the theme \"{name}\" rests on one study ([{studies[0]}]). Merge its codes into a theme with another study, or give \"singleStudyReason\": why that study alone answers a part of the objective no other study touches.";
        }
        return null;
    }

    // ─── 3. Second, independent coding ──────────────────────────────────────────────────────────────────

    private sealed class SecondAnswer { public List<SecondStudy>? Studies { get; set; } }
    private sealed class SecondStudy { public int Ref { get; set; } public List<string>? Themes { get; set; } }

    /// <summary>
    /// A second coder sees only the theme names and descriptions and each study's codes, and assigns the
    /// studies to themes. Agreement with the first assignment is Cohen's kappa over every study × theme cell.
    /// The second coding is reported; it does not change the themes.
    /// </summary>
    public static async Task<SecondCoding> SecondCodingAsync(IChatCompletionService chat, IReadOnlyList<Theme> themes, IReadOnlyList<ThematicCode> codes)
    {
        var studies = themes.SelectMany(t => t.Studies).Distinct().OrderBy(r => r).ToList();
        var themeIds = themes.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string themeList = string.Join("\n", themes.Select(t => $"{t.Id}: {t.Name}. {t.Description}"));
        string studyList = string.Join("\n", studies.Select(s => $"Study {s}: {string.Join("; ", codes.Where(c => c.Reference == s).Select(c => c.Label))}"));

        string prompt = $$"""
            You are the second, independent coder in the thematic synthesis of a systematic literature review. The themes are fixed; assign each study to every theme its codes belong to.

            {{PromptSafety.DataOnlyNotice}}

            THEMES:
            {{themeList}}

            {{PromptSafety.Wrap(studyList, "each study's codes")}}

            Respond ONLY with a minified JSON object listing every study:
            {"studies":[{"ref":3,"themes":["T1"]}]}
            """;
        try
        {
            var answer = await LlmJson.GetAsync<SecondAnswer>(chat, prompt, PrismaReviewEngine.JsonMode(0.0), a =>
            {
                if (a.Studies == null) return "studies must be a list.";
                var missing = studies.Except(a.Studies.Select(s => s.Ref)).ToList();
                if (missing.Count > 0) return $"every study needs an entry; missing: {string.Join(", ", missing.Take(15))}.";
                return (a.Studies.SelectMany(s => s.Themes ?? new()).FirstOrDefault(t => !themeIds.Contains(t.Trim())) is string bad) ? $"unknown theme id {bad}." : null;
            });
            var second = answer.Studies!.GroupBy(s => s.Ref).ToDictionary(g => g.Key,
                g => g.SelectMany(s => s.Themes ?? new()).Select(t => t.Trim().ToUpperInvariant()).ToHashSet());
            return CompareCodings(themes, second);
        }
        catch (Exception ex)
        {
            return new SecondCoding { Error = $"The second coding failed: {ex.GetType().Name}." };
        }
    }

    /// <summary>Cohen's kappa between the first assignment (theme study lists) and a second one, cell by cell.</summary>
    public static SecondCoding CompareCodings(IReadOnlyList<Theme> themes, IReadOnlyDictionary<int, HashSet<string>> second)
    {
        var result = new SecondCoding();
        var pairs = new List<(bool, bool)>();
        foreach (var study in themes.SelectMany(t => t.Studies).Distinct().OrderBy(r => r))
        {
            second.TryGetValue(study, out var assigned);
            foreach (var t in themes)
            {
                bool first = t.Studies.Contains(study);
                bool other = assigned != null && assigned.Contains(t.Id.ToUpperInvariant());
                pairs.Add((first, other));
                if (first != other)
                    result.Disagreements.Add($"Study {study}, {t.Id} ({t.Name}): {(first ? "first coding only" : "second coding only")}");
            }
        }
        result.CellsCompared = pairs.Count;
        result.Agreements = pairs.Count(p => p.Item1 == p.Item2);
        result.Kappa = pairs.Count == 0 ? null : Math.Round(ScreeningConfusion.From(pairs).CohensKappa, 3);
        return result;
    }
}

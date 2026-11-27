using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The parts of a review after its discussion (PRISMA 2020 items 23b and 23d, and a conclusion), written last from the
/// checked results and discussion. The limitations of the review processes (item 23c) are not here: they describe the
/// method, so they are written in code (<see cref="MethodsSectionWriter.ReviewProcessLimitations"/>).
/// </summary>
public sealed record ClosingSections(string EvidenceLimitations, string Implications, string FutureResearch, string Conclusion,
    IReadOnlyList<string>? Answers = null);

/// <summary>The title, abstract, rationale and objectives of a review, written last from its checked sections.</summary>
public sealed record FrontMatter(string Title, string Abstract, string Rationale, string Objectives);

/// <summary>
/// A summary for practitioners (G14 of the multivocal guidelines): one plain sentence on whom it is for, then a short
/// checklist, every item a cited sentence. <see cref="Text"/> is what the citation check reads: the items, one per line.
/// </summary>
public sealed record PractitionerSummary(string Lead, IReadOnlyList<string> Items)
{
    public string Text => string.Join("\n", Items);
}

/// <summary>
/// The writing stages every kind of review shares (module design M8): the grounded outline; one cited subsection per theme, written from
/// that theme's evidence only, with a fill pass for sources it does not cite yet; the discussion, written from the
/// subsections; and an automated peer review of every section with one revision of those that got medium or high
/// comments; and, last, the title, abstract, rationale and objectives from the checked sections. The stages read only <see cref="ReviewEvidence"/>, and every answer is checked in code before it is
/// used: citations must be in the reference list, and a revision may not drop a citation the section had.
/// </summary>
public static partial class ReviewWriter
{
    private sealed class SectionAnswer { public string? Heading { get; set; } public string? Text { get; set; } }
    private sealed class TextAnswer { public string? Text { get; set; } }
    private sealed class CritiqueAnswer { public List<CritiqueComment>? Comments { get; set; } }
    private sealed class CritiqueComment { public string Section { get; set; } = ""; public string Severity { get; set; } = ""; public string Issue { get; set; } = ""; public string Suggestion { get; set; } = ""; }

    private sealed class SummaryAnswer { public string? Lead { get; set; } public List<string>? Items { get; set; } }

    /// <summary>At most this many items in a summary for practitioners, so it stays a checklist.</summary>
    public const int MaxSummaryItems = 10;

    /// <summary>
    /// The summary for practitioners (G14), written from the finished, checked results and discussion only: a lead
    /// sentence on whom it is for and what it covers, without citations, then three to ten checklist items in plain
    /// words, each one sentence that ends with the sources the results cite for it. An item without a citation, a
    /// citation outside the reference list, or a number that is not in the results is rejected. Returns null when no
    /// acceptable answer comes back; the paper then has no summary rather than an unchecked one.
    /// </summary>
    public static async Task<PractitionerSummary?> WritePractitionerSummaryAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        string audience, IReadOnlyList<SynthesisSection> sections, string discussion, string referenceLines, int referenceCount, Action<string>? warn = null)
    {
        string results = string.Join("\n\n", sections.Select(x => $"### {x.Heading}\n{x.Text}")) + $"\n\n### Discussion\n{discussion}";
        string prompt = $$"""
            You are writing the summary for practitioners of {{profile.Review}}. Its results and discussion are finished and checked; write only what they say.

            REVIEW OBJECTIVE: "{{goal}}"
            PRIMARY TOPIC: "{{topic}}"
            WRITTEN FOR: {{audience}}
            {{PromptSafety.ReviewerInputNotice}}

            REFERENCE LIST (cite ONLY these numbers):
            {{referenceLines}}

            RESULTS AND DISCUSSION:
            {{results}}

            REQUIREMENTS:
            1. "lead": one plain sentence saying whom the summary is for and what it covers. No citations.
            2. "items": three to {{MaxSummaryItems}} checklist items a practitioner can act on or check against, in plain words and in the order of importance the results give them. Each item is ONE sentence of at most 35 words that ends with an inline [n] marker for the {{profile.Sources}} the results cite for it.
            3. Say how strong the support is where the results say so (for example, when a practice rests only on blog posts). Do not invent numbers, practices, tools or {{profile.Sources}}.
            4. Give no count of {{profile.Sources}} in an item (not "as 65 {{profile.Sources}} describe" or "noted in 12"): a count in the results belongs to one value of the map, and the item may not name that value. The [n] markers show the support; the results give the counts.
            5. {{CitationLoadRule(profile)}}
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"lead":"...","items":["... [n]","... [n, m]"]}
            """;
        try
        {
            using (LlmStage.Begin("practitioner-summary"))
            {
                var answer = await LlmJson.GetAsync<SummaryAnswer>(chat, prompt, LlmJson.JsonMode(0.3), a => SummaryProblem(a, results, referenceCount) is { } problem ? Noted(problem, "summary for practitioners", warn) : null, repairAttempts: 2);
                return new PractitionerSummary(
                    MethodsSectionWriter.StripMarkdownEmphasis(answer.Lead!.Trim()),
                    answer.Items!.Select(i => MethodsSectionWriter.StripMarkdownEmphasis(i.Trim())).ToList());
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"The summary for practitioners could not be written: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A count of sources in a summary item: "65 sources", "in 12 of the posts", "noted in 58". Run 50e9a8e3 (issue #465)
    /// gave three such counts, and each belonged to another value of the map than the practice the item named, so the
    /// check that the number appears in the results passed. Items now give no counts at all. Only one to three digits
    /// count, so a year ("2025 reports") or a model name ("GPT-4 papers") is not taken for a count.
    /// </summary>
    public static readonly Regex SourceCount = new(
        @"(?<![\w.,-])\d{1,3}\s+(?:of\s+(?:the\s+)?(?:\d+\s+)?)?(?:\w+\s+)?(?:sources?|stud(?:y|ies)|papers?|articles?|posts?|repositor(?:y|ies)|documents?|reports?|records?|pages?|threads?)\b|\b(?:noted|described|recommended|named|cited|mentioned|reported)\s+(?:in|by)\s+\d{1,3}(?![\d.,])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? SummaryProblem(SummaryAnswer a, string results, int referenceCount)
    {
        if (string.IsNullOrWhiteSpace(a.Lead) || a.Items == null) return "lead and items must both be given.";
        var items = a.Items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (items.Count < 3 || items.Count > MaxSummaryItems) return $"give three to {MaxSummaryItems} items; you gave {items.Count}.";
        if (ThematicSynthesis.CitedIn(a.Lead).Count > 0) return "the lead takes no citations.";
        int uncited = items.FindIndex(i => ThematicSynthesis.CitedIn(i).Count == 0);
        if (uncited >= 0) return $"item {uncited + 1} has no citation; every item ends with the [n] of the sources the results cite for it.";
        int tooLong = items.FindIndex(i => i.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 45);
        if (tooLong >= 0) return $"item {tooLong + 1} is too long; keep each item to one sentence of at most 35 words.";
        var outside = items.SelectMany(ThematicSynthesis.CitedIn).Where(r => r < 1 || r > referenceCount).Distinct().ToList();
        if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
        int counted = items.FindIndex(i => SourceCount.IsMatch(i));
        if (counted >= 0) return $"item {counted + 1} gives a count of sources (\"{SourceCount.Match(items[counted]).Value}\"); leave counts out of the items, the results give them.";
        int heavy = items.FindIndex(i => CitationLoadProblem(i) != null);
        if (heavy >= 0) return $"item {heavy + 1}: {CitationLoadProblem(items[heavy])}";
        var known = NumbersIn(results);
        var unknown = items.SelectMany(NumbersIn).Where(n => !known.Contains(n)).Distinct().ToList();
        return unknown.Count > 0 ? $"the items use {string.Join(", ", unknown)}, which is not in the results; use only their numbers." : null;
    }


    private sealed class FrontMatterAnswer { public string? Title { get; set; } public string? Abstract { get; set; } public string? Rationale { get; set; } public string? Objectives { get; set; } }

    /// <summary>
    /// The title, abstract, rationale and objectives, written last from the finished, checked sections, so they can
    /// only say what the review found. Numbers come from <paramref name="facts"/>, which the module writes in code
    /// (sources found, included, themes): a number in the abstract that is in neither the facts nor the sections is
    /// rejected, as is a citation outside the reference list. The rationale's citations are checked against their
    /// sources like any other sentence. A module can add its own check on the abstract (<paramref name="abstractRule"/>,
    /// null when the abstract is fine), such as the systematic review's rule that its record counts add up. With research
    /// questions the prompt lists them, and the objectives are written in code from them (<see cref="ObjectivesFromQuestions"/>)
    /// instead of by the model. The answers to the research questions and the conclusion, when they are written before it
    /// (issue #446), are given as checked text, so the abstract states the review's reading as they do. With the certainty of
    /// each answer (<paramref name="certainty"/>, issue #676) the abstract's general claim is held to it. Returns null when
    /// no acceptable answer comes back.
    /// </summary>
    public static async Task<FrontMatter?> WriteFrontMatterAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        string facts, IReadOnlyList<SynthesisSection> sections, string discussion, string referenceLines, int referenceCount, Action<string>? warn = null,
        Func<string, string?>? abstractRule = null, IReadOnlyList<string>? questions = null, IReadOnlyList<string>? answers = null, string? conclusion = null,
        IReadOnlyDictionary<int, string>? weakSources = null, IReadOnlyList<AnswerCertainty>? certainty = null)
    {
        string results = string.Join("\n\n", sections.Select(x => $"### {x.Heading}\n{x.Text}")) + $"\n\n### Discussion\n{discussion}";
        string readingBlock = (answers is { Count: > 0 } ? $"\n\nANSWERS TO THE RESEARCH QUESTIONS (written and checked before this step; the abstract's results and what they mean agree with them):\n{AnswerLines(answers, profile)}" : "")
            + (string.IsNullOrWhiteSpace(conclusion) ? "" : $"\n\nCONCLUSION (written and checked before this step; the abstract ends on the same reading, in its own words):\n{conclusion.Trim()}")
            + CertaintyBlock(certainty);
        string? codeObjectives = ObjectivesFromQuestions(questions, profile);
        string objectivesRule = codeObjectives == null ? "\"objectives\": the objective and what the review set out to answer, in two or three sentences, without citations."
            : "\"objectives\" are written in code from the research questions above; do not write them.";
        string objectivesField = codeObjectives == null ? ",\"objectives\":\"...\"" : "";
        string prompt = $$"""
            You are writing the title, abstract, rationale and objectives of {{profile.Review}}. Its results and discussion are finished and checked; write only what they and the facts below say.

            {{AimBlock(goal, questions, profile)}}
            PRIMARY TOPIC: "{{topic}}"
            {{PromptSafety.ReviewerInputNotice}}

            FACTS ABOUT THE REVIEW (counted in code; the only numbers about the review you may use):
            {{facts}}{{WeakSourcesBlock(weakSources, profile)}}

            REFERENCE LIST (cite ONLY these numbers):
            {{referenceLines}}

            RESULTS AND DISCUSSION:
            {{results}}{{readingBlock}}

            REQUIREMENTS:
            1. "title": one line, at most 25 words, no full stop, naming the topic and the kind of review.
            2. "abstract": one paragraph of 150 to 250 words: background, method, main results by theme, and what they mean. No citations. Use no number that is not in the facts or the results above. {{ZeroCountRule}} {{NumeralOpeningRule}}{{HedgeRule(certainty, "the abstract")}}
            3. {{RationaleRule(profile)}} {{ProcessFactsRule(profile)}}{{WeakOpeningRule(weakSources, profile)}}
            4. {{objectivesRule}}
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"title":"...","abstract":"...","rationale":"... [n] ..."{{objectivesField}}}
            """;
        string allowedNumbers = facts + "\n" + results + readingBlock;
        try
        {
            using (LlmStage.Begin("front-matter"))
            {
                var answer = await LlmJson.GetAsync<FrontMatterAnswer>(chat, prompt, LlmJson.JsonMode(0.3), WithSoftGuards<FrontMatterAnswer>(
                    a => Noted(FrontMatterProblem(a, allowedNumbers, referenceCount, codeObjectives == null, facts), "front-matter call", warn) ?? abstractRule?.Invoke(a.Abstract!),
                    "front-matter call", warn, 2,
                    (a => WeakOpeningProblem(a.Rationale, weakSources, profile), "a rationale that opens on one weak source"),
                    (a => RationaleListingProblem(a.Rationale, profile), "a rationale that lists the sources"),
                    (a => RationaleOpeningProblem(a.Rationale), "a rationale that opens on a word pointing back"),
                    (a => ZeroCountProblem(a.Abstract, "the abstract"), "a count of zero"),
                    (a => OverclaimProblem(a.Abstract, certainty, "the abstract"), "a claim stronger than the certainty of the evidence"),
                    // Issues #746 and #747: the review's own process is in the methods text, not in a cited sentence.
                    (a => NumeralOpeningProblem(a.Abstract, "the abstract"), "an abstract sentence that opens on a numeral"),
                    (a => CitedReadingBasisProblem(a.Rationale, "the rationale"), "a cited rationale sentence on what the review read")),
                    repairAttempts: 3);
                return new FrontMatter(
                    answer.Title!.Trim().TrimEnd('.'),
                    MethodsSectionWriter.StripMarkdownEmphasis(answer.Abstract!.Trim()),
                    MethodsSectionWriter.StripMarkdownEmphasis(answer.Rationale!.Trim()),
                    codeObjectives ?? MethodsSectionWriter.StripMarkdownEmphasis(answer.Objectives!.Trim()));
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Front matter could not be written: {ex.Message}");
            return null;
        }
    }

    private static string? FrontMatterProblem(FrontMatterAnswer a, string allowedNumbers, int referenceCount, bool objectives = true, string facts = "")
    {
        if (string.IsNullOrWhiteSpace(a.Title) || string.IsNullOrWhiteSpace(a.Abstract) || string.IsNullOrWhiteSpace(a.Rationale))
            return objectives ? "title, abstract, rationale and objectives must all be given." : "title, abstract and rationale must all be given.";
        if (objectives && string.IsNullOrWhiteSpace(a.Objectives)) return "title, abstract, rationale and objectives must all be given.";
        if (a.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 25) return "the title must be at most 25 words.";
        if (ThematicSynthesis.CitedIn(a.Abstract).Count > 0 || ThematicSynthesis.CitedIn(a.Objectives).Count > 0) return "the abstract and objectives take no citations.";
        if (ThematicSynthesis.CitedIn(a.Rationale).Count == 0) return "the rationale has no citation; every sentence of it ends with the [n] of the sources the results cite for it.";
        var outside = ThematicSynthesis.CitedIn(a.Rationale).Where(r => r < 1 || r > referenceCount).ToList();
        if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
        if (CitationLoadProblem(a.Rationale, part: "the rationale") is { } load) return load;
        // The rationale states the problem and the gap; a result figure belongs to the results (the audit of two real runs).
        var figures = NumbersIn(a.Rationale).Where(n => !NumbersIn(facts).Contains(n)).ToList();
        if (figures.Count > 0) return $"the rationale uses {string.Join(", ", figures)}; it states the problem and the gap, not the results, so leave out result figures.";
        var known = NumbersIn(allowedNumbers);
        var unknown = NumbersIn(a.Abstract).Where(n => !known.Contains(n)).ToList();
        return unknown.Count > 0 ? $"the abstract uses {string.Join(", ", unknown)}, which is in neither the facts nor the results; use only those numbers." : null;
    }

    /// <summary>
    /// The rationale with only its cited sentences (issue #248), and its gap: in a live run two uncited sentences came
    /// from the model's general knowledge, not from the sources. The last sentence, when uncited, is the rationale's one
    /// statement of what is not yet known, and is kept when it reports no finding (<see cref="UncitedAttribution"/>, with
    /// the review's own counts in <paramref name="facts"/>); every other uncited sentence is left out, and the paragraphs
    /// are kept. <paramref name="log"/> records what was left out and whether the gap was kept, for the writing ledger,
    /// or is null when every sentence is cited. The goal, audience and research questions are in the objectives.
    /// </summary>
    public static string CitedSentencesOnly(string rationale, out StyleDeltaLog? log, string facts = "")
    {
        log = null;
        if (string.IsNullOrWhiteSpace(rationale)) return rationale;
        var paragraphs = Regex.Split(rationale.Trim(), @"\n\s*\n").Select(p => CitationSupportChecker.SplitSentences(p).ToList()).Where(p => p.Count > 0).ToList();
        string? gap = paragraphs.LastOrDefault()?.Last() is { } last && ThematicSynthesis.CitedIn(last).Count == 0 ? last : null;
        string? gapProblem = gap == null ? null : UncitedAttribution(gap, facts);
        bool Keep(string s) => ThematicSynthesis.CitedIn(s).Count > 0 || (ReferenceEquals(s, gap) && gapProblem == null);
        var dropped = paragraphs.SelectMany(p => p).Where(s => !Keep(s)).ToList();
        if (gap == null && dropped.Count == 0) return rationale;
        string result = string.Join("\n\n", paragraphs.Select(p => string.Join(" ", p.Where(Keep))).Where(p => p.Length > 0));
        var notes = new List<string>();
        if (dropped.Count > 0)
            notes.Add($"Left out in code, because {(dropped.Count == 1 ? "it carries" : "they carry")} no citation: " + string.Join(" ", dropped.Select(d => $"\"{d}\"")));
        if (gap != null)
            notes.Add(gapProblem == null ? $"Kept as the statement of the gap, because it reports no finding: \"{gap}\"" : $"The closing sentence was not kept as the gap: {gapProblem}");
        log = new StyleDeltaLog(RationaleCitedOnlyField, rationale, result, Applied: dropped.Count > 0, Note: string.Join(" ", notes));
        return dropped.Count == 0 ? rationale : result;
    }

    /// <summary>The writing ledger's name for the step that keeps the rationale's cited sentences only.</summary>
    public const string RationaleCitedOnlyField = "Rationale (cited sentences only)";

    /// <summary>The numbers written as digits in a text, citation markers left out.</summary>
    public static HashSet<string> NumbersIn(string text) =>
        System.Text.RegularExpressions.Regex.Matches(System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]", ""), @"\d+(?:[.,]\d+)?")
            .Select(m => m.Value.TrimEnd('.', ',')).ToHashSet();
}

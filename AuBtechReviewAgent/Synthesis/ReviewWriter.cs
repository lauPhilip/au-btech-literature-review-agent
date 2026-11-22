using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// How the writing prompts name a kind of review and its parts. Everything else in the prompts, and every check of
/// the answers, is the same for every kind of review. The defaults are the systematic review's words, so its prompts
/// stay exactly as they were.
/// </summary>
public sealed record WritingProfile
{
    /// <summary>The systematic review's words.</summary>
    public static readonly WritingProfile Systematic = new();

    /// <summary>The review, with its article, e.g. "a systematic literature review".</summary>
    public string Review { get; init; } = "a systematic literature review";

    /// <summary>The review in short, with its article, e.g. "a systematic review".</summary>
    public string ReviewShort { get; init; } = "a systematic review";

    /// <summary>Where a theme subsection goes, e.g. "the Results & Synthesis section (PRISMA 2020 Item 20a)".</summary>
    public string ResultsSection { get; init; } = "the Results & Synthesis section (PRISMA 2020 Item 20a)";

    /// <summary>The discussion, e.g. "the Discussion section (PRISMA 2020 Item 23a)".</summary>
    public string DiscussionSection { get; init; } = "the Discussion section (PRISMA 2020 Item 23a)";

    /// <summary>What the review calls one source and several, e.g. "study" and "studies".</summary>
    public string Source { get; init; } = "study";
    public string Sources { get; init; } = "studies";

    /// <summary>Where the facts about the evidence base come from, e.g. "from the data extraction".</summary>
    public string BaseFactsFrom { get; init; } = "from the data extraction";

    /// <summary>What the excerpts of the sources' texts are, e.g. "excerpts from the included papers".</summary>
    public string SourceTexts { get; init; } = "excerpts from the included papers";

    /// <summary>
    /// What the discussion's paragraphs cover, in order. The systematic review's discussion is PRISMA item 23a only: the
    /// limitations, implications, future research and conclusion are written after it by <see cref="ReviewWriter.WriteClosingAsync"/>.
    /// </summary>
    public string DiscussionParts { get; init; } =
        "(a) what the themes together say about the objective, (b) where the themes connect, reinforce or contradict each other, (c) how strong the evidence behind the main findings is. Do not write the limitations, the implications, the directions for future research or a conclusion: they are separate sections written after this one.";
}

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
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"lead":"...","items":["... [n]","... [n, m]"]}
            """;
        try
        {
            using (LlmStage.Begin("practitioner-summary"))
            {
                var answer = await LlmJson.GetAsync<SummaryAnswer>(chat, prompt, LlmJson.JsonMode(0.3), a => SummaryProblem(a, results, referenceCount), repairAttempts: 2);
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
    /// null when the abstract is fine), such as the systematic review's rule that its record counts add up. Returns
    /// null when no acceptable answer comes back.
    /// </summary>
    public static async Task<FrontMatter?> WriteFrontMatterAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        string facts, IReadOnlyList<SynthesisSection> sections, string discussion, string referenceLines, int referenceCount, Action<string>? warn = null,
        Func<string, string?>? abstractRule = null)
    {
        string results = string.Join("\n\n", sections.Select(x => $"### {x.Heading}\n{x.Text}")) + $"\n\n### Discussion\n{discussion}";
        string prompt = $$"""
            You are writing the title, abstract, rationale and objectives of {{profile.Review}}. Its results and discussion are finished and checked; write only what they and the facts below say.

            REVIEW OBJECTIVE: "{{goal}}"
            PRIMARY TOPIC: "{{topic}}"
            {{PromptSafety.ReviewerInputNotice}}

            FACTS ABOUT THE REVIEW (counted in code; the only numbers about the review you may use):
            {{facts}}

            REFERENCE LIST (cite ONLY these numbers):
            {{referenceLines}}

            RESULTS AND DISCUSSION:
            {{results}}

            REQUIREMENTS:
            1. "title": one line, at most 25 words, no full stop, naming the topic and the kind of review.
            2. "abstract": one paragraph of 150 to 250 words: background, method, main results by theme, and what they mean. No citations. Use no number that is not in the facts or the results above.
            3. "rationale": one or two paragraphs on why the review is needed, from what the {{profile.Sources}} report; every sentence that states something from the literature ends with an inline [n] marker, using the {{profile.Sources}} the results cite for it.
            4. "objectives": the objective and what the review set out to answer, in two or three sentences, without citations.
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"title":"...","abstract":"...","rationale":"... [n] ...","objectives":"..."}
            """;
        string allowedNumbers = facts + "\n" + results;
        try
        {
            using (LlmStage.Begin("front-matter"))
            {
                var answer = await LlmJson.GetAsync<FrontMatterAnswer>(chat, prompt, LlmJson.JsonMode(0.3), a => FrontMatterProblem(a, allowedNumbers, referenceCount) ?? abstractRule?.Invoke(a.Abstract!), repairAttempts: 2);
                return new FrontMatter(
                    answer.Title!.Trim().TrimEnd('.'),
                    MethodsSectionWriter.StripMarkdownEmphasis(answer.Abstract!.Trim()),
                    MethodsSectionWriter.StripMarkdownEmphasis(answer.Rationale!.Trim()),
                    MethodsSectionWriter.StripMarkdownEmphasis(answer.Objectives!.Trim()));
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Front matter could not be written: {ex.Message}");
            return null;
        }
    }

    private static string? FrontMatterProblem(FrontMatterAnswer a, string allowedNumbers, int referenceCount)
    {
        if (string.IsNullOrWhiteSpace(a.Title) || string.IsNullOrWhiteSpace(a.Abstract) || string.IsNullOrWhiteSpace(a.Rationale) || string.IsNullOrWhiteSpace(a.Objectives))
            return "title, abstract, rationale and objectives must all be given.";
        if (a.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 25) return "the title must be at most 25 words.";
        if (ThematicSynthesis.CitedIn(a.Abstract).Count > 0 || ThematicSynthesis.CitedIn(a.Objectives).Count > 0) return "the abstract and objectives take no citations.";
        var outside = ThematicSynthesis.CitedIn(a.Rationale).Where(r => r < 1 || r > referenceCount).ToList();
        if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
        var known = NumbersIn(allowedNumbers);
        var unknown = NumbersIn(a.Abstract).Where(n => !known.Contains(n)).ToList();
        return unknown.Count > 0 ? $"the abstract uses {string.Join(", ", unknown)}, which is in neither the facts nor the results; use only those numbers." : null;
    }

    /// <summary>The numbers written as digits in a text, citation markers left out.</summary>
    public static HashSet<string> NumbersIn(string text) =>
        System.Text.RegularExpressions.Regex.Matches(System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]", ""), @"\d+(?:[.,]\d+)?")
            .Select(m => m.Value.TrimEnd('.', ',')).ToHashSet();

    /// <summary>
    /// One critique of all subsections and the discussion, then a revision of each section that received medium or
    /// high comments. A revision that drops a citation the section had (and so lowers coverage) or cites outside the
    /// reference list is rejected, and the original is kept. The sections are revised in place; the revised
    /// discussion is returned with the log.
    /// </summary>
    public static async Task<(PeerReviewLog Log, string Discussion)> PeerReviewAsync(IChatCompletionService chat, WritingProfile profile,
        List<SynthesisSection> sections, string discussion, ReviewEvidence evidence, int referenceCount, int parallelism, Action<string>? warn = null)
    {
        string AllSynthesis() => string.Join("\n\n", sections.Select(x => $"### {x.Heading}\n{x.Text}"));
        var log = new PeerReviewLog
        {
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            SynthesisBefore = AllSynthesis(),
            DiscussionBefore = discussion,
            Verdict = "No changes applied",
        };
        var ids = sections.Select(x => x.Field).Append("discussion").ToList();
        // The critique names sections by id; the reader never sees those ids, so a revision names a section by its heading.
        var headings = sections.GroupBy(x => x.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Heading, StringComparer.OrdinalIgnoreCase);
        string Named(string text) => NameSections(text, headings);
        string sectionsText = string.Join("\n\n", sections.Select(x => $"[{x.Field}] {x.Heading}\n{x.Text}"))
                              + $"\n\n[discussion] Discussion\n{discussion}";

        string critiquePrompt = $$"""
            You are a strict but constructive peer reviewer for {{profile.Review}}. Review each section below (theme subsections of the results, and the discussion) for: (a) depth: does it synthesise and compare rather than list, (b) whether each specific claim carries an inline [n] citation, (c) over-claiming beyond what a {{profile.Source}} could show, (d) coherence and overlap between sections, (e) whether the discussion interprets rather than repeats.

            REFERENCE LIST (valid citation numbers):
            {{evidence.ReferenceLines()}}

            SECTIONS (the id in brackets is the section id):
            {{sectionsText}}

            Respond ONLY with a valid minified JSON object:
            { "comments": [ { "section": "one of: {{string.Join(", ", ids)}}", "severity": "high, medium, or low", "issue": "what is wrong or weak", "suggestion": "how to fix it" } ] }
            If a section is already strong, return few or no comments for it.
            """;
        try
        {
            var answer = await LlmJson.GetAsync<CritiqueAnswer>(chat, critiquePrompt, LlmJson.JsonMode(0.2), a =>
                a.Comments == null ? "comments must be a list (it may be empty)." :
                a.Comments.Any(c => !ids.Contains(c.Section.Trim(), StringComparer.OrdinalIgnoreCase)) ? $"each comment's section must be one of: {string.Join(", ", ids)}." : null);
            log.Comments = answer.Comments!.Select(c => new PeerReviewComment(c.Section.Trim().ToLowerInvariant(), c.Severity, c.Issue, c.Suggestion)).ToList();
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Peer-review critique skipped: {ex.Message}");
        }

        var actionable = log.Comments
            .Where(c => !c.Severity.Equals("low", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Section).ToList();
        if (actionable.Count == 0)
        {
            log.SynthesisAfter = log.SynthesisBefore;
            log.DiscussionAfter = log.DiscussionBefore;
            log.Verdict = log.Comments.Count == 0 ? "No revisions needed - reviewer found no actionable issues" : "Only low-severity comments; no revisions applied";
            return (log, discussion);
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, parallelism));
        var revisions = actionable.Select(group => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try
            {
                var section = sections.FirstOrDefault(x => x.Field == group.Key);
                string before = section?.Text ?? discussion;
                string feedback = Named(string.Join("\n", group.Select(c => $"- [{c.Severity}] {c.Issue} -> {c.Suggestion}")));
                string revisePrompt = $$"""
                    You are the author revising one section of {{profile.ReviewShort}} in response to peer-review feedback. Apply the feedback. Keep every existing inline [n] citation, cite ONLY numbers from the reference list, and do not invent claims, numbers or {{profile.Sources}}.

                    REFERENCE LIST (valid citation numbers):
                    {{evidence.ReferenceLines()}}

                    FACTS ABOUT THE EVIDENCE BASE ({{profile.BaseFactsFrom}}; do not change them, and describe the {{profile.Sources}} only as they say):
                    {{evidence.BaseFacts}}

                    PEER-REVIEW FEEDBACK:
                    {{feedback}}

                    CURRENT SECTION ({{(section == null ? "Discussion" : section.Heading)}}):
                    {{before}}

                    {{ProseCleaner.PlainProseRule}}
                    Refer to another section only by its heading in words, never by an id.
                    Respond ONLY with a valid minified JSON object:
                    {"text":"the revised section with inline [n] citations"}
                    """;
                try
                {
                    var answer = await LlmJson.GetAsync<TextAnswer>(chat, revisePrompt, LlmJson.JsonMode(0.3), a =>
                        string.IsNullOrWhiteSpace(a.Text) ? "text must not be empty." : null);
                    string after = Named(MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim()));
                    string? problem = ThematicSynthesis.RevisionProblem(before, after, referenceCount);
                    return new SectionRevision(group.Key, before, problem == null ? after : before, problem == null,
                        problem == null ? $"Revised in response to {group.Count()} comment(s)." : $"Revision rejected: {problem}; original kept.");
                }
                catch (Exception ex)
                {
                    return new SectionRevision(group.Key, before, before, false, $"Revision failed ({ex.GetType().Name}); original kept.");
                }
            }
            finally { throttle.Release(); }
        })).ToList();

        foreach (var task in revisions)
        {
            var revision = await task;
            log.SectionRevisions.Add(revision);
            if (!revision.Applied) continue;
            var section = sections.FirstOrDefault(x => x.Field == revision.Section);
            if (section != null) section.Text = revision.After;
            else discussion = revision.After;
        }
        log.SectionRevisions = log.SectionRevisions.OrderBy(r => ids.IndexOf(r.Section)).ToList();
        log.SynthesisAfter = AllSynthesis();
        log.DiscussionAfter = discussion;
        int applied = log.SectionRevisions.Count(r => r.Applied);
        log.Verdict = $"Revisions applied to {applied} of {log.SectionRevisions.Count} section(s) with medium or high comments";
        return (log, discussion);
    }

    /// <summary>Replaces section ids such as "synthesis-2" (with or without brackets) by the section's heading in quotes.</summary>
    public static string NameSections(string text, IReadOnlyDictionary<string, string> headings)
    {
        if (headings.Count == 0 || string.IsNullOrEmpty(text)) return text;
        string ids = string.Join("|", headings.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return Regex.Replace(text, $@"\[?\b({ids})\b\]?", m => headings.TryGetValue(m.Groups[1].Value, out var heading) ? $"the section \u201c{heading}\u201d" : m.Value, RegexOptions.IgnoreCase);
    }
}

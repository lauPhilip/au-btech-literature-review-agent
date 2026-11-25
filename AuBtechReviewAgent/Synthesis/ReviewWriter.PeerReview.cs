using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The automated peer review of every section, with one revision of those that got medium or high comments.</summary>
public static partial class ReviewWriter
{
    /// <summary>
    /// One critique of all subsections and the discussion, then a revision of each section that received medium or
    /// high comments. A revision that drops a citation the section had (and so lowers coverage) or cites outside the
    /// reference list is rejected, and the original is kept. The sections are revised in place; the revised
    /// discussion is returned with the log. With research questions, the critique also sees the objective and the
    /// questions, and judges whether each section serves its question. The critique gets the facts about the evidence base,
    /// the sources left out of the themes and the sources of each theme (issue #447): in run cce1c6e4 it saw none of
    /// them, asserted a false fact about the study designs, and the revision applied it. A theme revision that cites a
    /// source outside its theme is rejected in code.
    /// </summary>
    public static async Task<(PeerReviewLog Log, string Discussion)> PeerReviewAsync(IChatCompletionService chat, WritingProfile profile,
        List<SynthesisSection> sections, string discussion, ReviewEvidence evidence, int referenceCount, int parallelism, Action<string>? warn = null,
        string goal = "", IReadOnlyList<string>? questions = null, IReadOnlyList<string>? answers = null)
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
        string sectionsText = string.Join("\n\n", sections.Select(x => $"[{x.Field}] {x.Heading}{ThemeSourcesLine(x, evidence, profile)}\n{x.Text}"))
                              + $"\n\n[discussion] Discussion\n{discussion}";
        string critiqueAim = questions is not { Count: > 0 } ? "" : "\n\n" + AimBlock(goal, questions, profile) +
            (answers is { Count: > 0 } ? $"\nANSWERS TO THE RESEARCH QUESTIONS (written with the discussion and checked; not under review here):\n{AnswerLines(answers, profile)}" : "") +
            "\nAlso review (f) whether each section serves the research question it is for: a theme subsection answers its questions, and the discussion builds on the answers rather than restating them.";

        string critiquePrompt = $$"""
            You are a strict but constructive peer reviewer for {{profile.Review}}. Review each section below (theme subsections of the results, and the discussion) for: (a) depth: does it synthesise and compare rather than list ({{OneSourceThemeRule(profile)}}), (b) whether each specific claim carries an inline [n] citation, (c) over-claiming beyond what a {{profile.Source}} could show, (d) coherence and overlap between sections, (e) whether the discussion interprets rather than repeats.{{critiqueAim}}

            {{CritiqueFactsRule(profile)}}

            REFERENCE LIST (valid citation numbers):
            {{evidence.ReferenceLines()}}

            FACTS ABOUT THE EVIDENCE BASE ({{profile.BaseFactsFrom}}; the only facts about the {{profile.Sources}} and their designs; do not contradict them):
            {{evidence.BaseFacts}}{{LeftOutBlock(evidence, profile)}}

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
                var allowed = section == null ? null : ThemeSources(section, evidence);
                string themeRule = allowed == null ? "" : $"\n\nThis section reports one theme: cite only its {profile.Sources}, {Markers(allowed)}, even where the feedback asks for others.";
                string feedback = Named(string.Join("\n", group.Select(c => $"- [{c.Severity}] {c.Issue} -> {c.Suggestion}")));
                string revisePrompt = $$"""
                    You are the author revising one section of {{profile.ReviewShort}} in response to peer-review feedback. Apply the feedback. Keep every existing inline [n] citation, cite ONLY numbers from the reference list, and do not invent claims, numbers or {{profile.Sources}}. {{CitationLoadRule(profile)}}

                    REFERENCE LIST (valid citation numbers):
                    {{evidence.ReferenceLines()}}

                    FACTS ABOUT THE EVIDENCE BASE ({{profile.BaseFactsFrom}}; do not change them, and describe the {{profile.Sources}} only as they say):
                    {{evidence.BaseFacts}}

                    PEER-REVIEW FEEDBACK:
                    {{feedback}}

                    CURRENT SECTION ({{(section == null ? "Discussion" : section.Heading)}}):
                    {{before}}{{themeRule}}

                    {{ProseCleaner.PlainProseRule}}
                    Refer to another section only by its heading in words, never by an id.
                    Respond ONLY with a valid minified JSON object:
                    {"text":"the revised section with inline [n] citations"}
                    """;
                try
                {
                    var answer = await LlmJson.GetAsync<TextAnswer>(chat, revisePrompt, LlmJson.JsonMode(0.3), a =>
                        string.IsNullOrWhiteSpace(a.Text) ? "text must not be empty." : CitationLoadSendBack(a.Text, "peer-review revision", warn, before));
                    string after = Named(MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim()));
                    string? problem = ThematicSynthesis.RevisionProblem(before, after, referenceCount)
                        ?? (allowed == null ? null : OutsideTheme(before, after, allowed, profile))
                        ?? (section == null ? LeftOutCited(after, evidence) : null)
                        ?? (section == null && ReadingBasisSentence(after) is { } basis ? ReadingBasisProblem(basis, profile) : null)
                        ?? (section == null ? UncitedAttribution(after, evidence.BaseFacts) : null)
                        ?? (section == null && OneLimitationsParagraph(after, out _) != after ? "it describes the limitations in more than one paragraph" : null);
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

    /// <summary>The sources of the theme a section reports, or null when it reports none the evidence knows.</summary>
    private static IReadOnlyList<int>? ThemeSources(SynthesisSection section, ReviewEvidence evidence) =>
        evidence.Themes.FirstOrDefault(t => t.Id.Length > 0 && t.Id == section.ThemeId)?.Sources;

    private static string Markers(IEnumerable<int> references) => string.Join(", ", references.OrderBy(r => r).Select(r => $"[{r}]"));

    /// <summary>The line after a section's heading in the critique that names its theme's sources; empty for a section without a theme.</summary>
    private static string ThemeSourcesLine(SynthesisSection section, ReviewEvidence evidence, WritingProfile profile) =>
        ThemeSources(section, evidence) is { Count: > 0 } sources
            ? $" ({profile.Sources} of this theme: {Markers(sources)}{(sources.Count == 1 ? $"; it rests on one {profile.Source}" : "")})"
            : "";

    /// <summary>The critique's rule for a theme on one source (issue #447): such a theme has nothing to compare.</summary>
    public static string OneSourceThemeRule(WritingProfile profile) =>
        $"for a theme that rests on one {profile.Source}, judge only whether it reports that {profile.Source} faithfully and says what one {profile.Source} can show; do not ask it to compare or to add depth from other {profile.Sources}";

    /// <summary>The critique's rule on facts (issue #447): it may judge the sources only against the facts it is given.</summary>
    public static string CritiqueFactsRule(WritingProfile profile) =>
        $"Judge what a section says about the {profile.Sources} (how many there are, their designs, what they were read from) only against the facts about the evidence base below, and never state a fact about them that the facts do not give. A theme subsection may cite only the {profile.Sources} of its theme, named after its heading: do not ask it to add others, and do not ask any section to use a {profile.Source} left out of the themes.";

    /// <summary>
    /// Why a theme revision cites a source outside its theme, or null (issue #447). A source the section already cited
    /// before the revision stays allowed. Public for unit tests.
    /// </summary>
    public static string? OutsideTheme(string before, string after, IReadOnlyList<int> themeSources, WritingProfile profile)
    {
        var outside = ThematicSynthesis.CitedIn(after).Except(themeSources).Except(ThematicSynthesis.CitedIn(before)).ToList();
        return outside.Count == 0 ? null
            : $"it cites {Markers(outside)}, which {(outside.Count == 1 ? "is" : "are")} not among the {profile.Sources} of this theme ({Markers(themeSources)})";
    }

    /// <summary>Replaces section ids such as "synthesis-2" (with or without brackets) by the section's heading in quotes.</summary>
    public static string NameSections(string text, IReadOnlyDictionary<string, string> headings)
    {
        if (headings.Count == 0 || string.IsNullOrEmpty(text)) return text;
        string ids = string.Join("|", headings.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return Regex.Replace(text, $@"\[?\b({ids})\b\]?", m => headings.TryGetValue(m.Groups[1].Value, out var heading) ? $"the section \u201c{heading}\u201d" : m.Value, RegexOptions.IgnoreCase);
    }
}

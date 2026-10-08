using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// Thematic synthesis stage: codes the included studies from their verified findings, groups the codes into
/// themes, writes one cited subsection per theme from that theme's studies only, makes sure every study of a
/// theme is cited (one fill pass), writes the discussion from the subsections, and repairs cited sentences the
/// support check rejects. Every step is recorded (thematic-codebook.json, peer-review-feedback.json,
/// citation-audit.json) and every model answer is checked in code before it is used.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>Thematic synthesis, dual coding and citation repair settings ("Synthesis" section).</summary>
    public SynthesisOptions Synthesis { get; init; } = new();

    private sealed class ThematicResult
    {
        public ThematicCodebook Codebook { get; init; } = new();
        public List<SynthesisSection> Sections { get; } = new();
        public string Discussion { get; set; } = "";
        public PeerReviewLog? PeerReview { get; set; }
    }

    private sealed class SectionAnswer { public string? Heading { get; set; } public string? Text { get; set; } }
    private sealed class TextAnswer { public string? Text { get; set; } }
    private sealed class SectionCritiqueAnswer { public List<CritiqueComment>? Comments { get; set; } }

    /// <summary>
    /// Runs coding, codebook, optional second coding, theme subsections with the coverage fill pass, the
    /// discussion and the per-section peer review. Returns a result without sections when the synthesis has
    /// to fall back to the single-pass writer (the reason is in <see cref="ThematicCodebook.FallbackReason"/>).
    /// </summary>
    private async Task<ThematicResult> RunThematicSynthesisAsync(Guid runId,
        IChatCompletionService chat, string query, string objective, ReviewState state,
        IReadOnlyList<ReferencedPaper> papers, int referenceCount)
    {
        var book = new ThematicCodebook();
        var result = new ThematicResult { Codebook = book };
        var extractions = state.Extractions ?? new List<StudyExtraction>();
        string goal = string.IsNullOrWhiteSpace(objective) ? query : objective;

        // 1. Coding
        List<ThematicCode> codes;
        using (LlmStage.Begin("thematic-coding"))
        {
            ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 1, 0), $"Coding {papers.Count} studies");
            var (c, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, goal, papers, extractions,
                Synthesis.CodingBatchSize, Llm.ScreeningParallelism,
                (done, total) => ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 1, (double)done / total), $"Coding studies: {Of(done, total, "batches")}"));
            codes = c;
            book.Codes = codes;
            book.Uncoded.AddRange(uncoded);
        }
        if (codes.Count == 0)
        {
            book.FallbackReason = "no study could be coded";
            return result;
        }

        // 2. Codebook
        try
        {
            ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 2, 0), $"Grouping {codes.Count} codes into themes");
            using (LlmStage.Begin("thematic-codebook"))
            {
                var (themes, unplaced) = await ThematicSynthesis.BuildCodebookAsync(chat, goal, codes, Math.Max(2, Synthesis.MaxThemes));
                book.Themes = themes;
                book.Uncoded.AddRange(unplaced);
                book.Uncoded = book.Uncoded.OrderBy(u => u.Reference).ToList();
            }
        }
        catch (Exception ex)
        {
            book.FallbackReason = $"the codebook could not be built ({SanitizeLogMessage(ex.Message)})";
            _log.LogWarning("Thematic codebook failed, falling back to the single-pass synthesis: {Message}", SanitizeLogMessage(ex.Message));
            return result;
        }
        if (book.Themes.Count == 0)
        {
            book.FallbackReason = "the codebook had no themes";
            return result;
        }

        // 3. Second, independent coding (reported, does not change the themes)
        if (Synthesis.DualCoding)
        {
            using (LlmStage.Begin("thematic-coding-second"))
                book.SecondCoding = await ThematicSynthesis.SecondCodingAsync(chat, book.Themes, codes);
        }

        // 4. One subsection per theme, each with its own coverage fill pass. Written in parallel, assembled
        //    in theme order.
        var evidence = SystematicEvidence.Build(book, state.SynthesizedRecords, papers, extractions);
        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        int written = 0;
        ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 3, 0), $"Writing {book.Themes.Count} theme subsections");
        var work = book.Themes.Select((theme, index) => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try
            {
                var written1 = await WriteThemeAsync(chat, goal, evidence, theme, evidence.Themes[index], index + 1, referenceCount);
                int n = Interlocked.Increment(ref written);
                ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 3, (double)n / book.Themes.Count), $"Theme subsections: {Of(n, book.Themes.Count, "written")}");
                return written1;
            }
            finally { throttle.Release(); }
        })).ToList();
        foreach (var task in work)
        {
            var (section, coverage) = await task;
            book.Coverage.Add(coverage);
            if (section != null) result.Sections.Add(section);
        }
        if (result.Sections.Count == 0)
        {
            book.FallbackReason = "no theme subsection could be written";
            return result;
        }

        // 5. Discussion from the subsections
        ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 4, 0), "Writing the discussion");
        using (LlmStage.Begin("discussion"))
            result.Discussion = await WriteDiscussionAsync(chat, query, goal, result.Sections, evidence, referenceCount);

        // 6. Peer review, per section, with the same coverage guard as the fill pass
        ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 5, 0), "Automated peer review");
        using (LlmStage.Begin("automated-peer-review"))
            result.PeerReview = await PeerReviewSectionsAsync(chat, result, evidence, referenceCount);

        return result;
    }

    private async Task<(SynthesisSection? Section, ThemeCoverage Coverage)> WriteThemeAsync(
        IChatCompletionService chat, string goal, ReviewEvidence evidence, Theme theme, EvidenceTheme evidenceTheme, int number, int referenceCount)
    {
        string field = $"synthesis-{number}";
        var coverage = new ThemeCoverage { ThemeId = theme.Id, Field = field, Studies = theme.Studies.ToList() };
        int excerpts = theme.Studies.Count <= 12 ? 2 : 1;
        string themeEvidence = string.Join("\n", theme.Studies.Select(r => evidence.SourceBlock(r, evidenceTheme, excerpts)));
        string otherThemes = string.Join("; ", evidence.Themes.Where(t => t.Id != theme.Id).Select(t => t.Name));
        int paragraphs = ThematicSynthesis.ParagraphsFor(theme.Studies.Count);
        string studyList = string.Join(", ", theme.Studies.Select(r => $"[{r}]"));

        string prompt = $$"""
            You are writing one subsection of the Results & Synthesis section (PRISMA 2020 Item 20a) of a systematic literature review. The review used thematic synthesis; this subsection reports one theme.

            REVIEW OBJECTIVE: "{{goal}}"
            {{PromptSafety.ReviewerInputNotice}}
            THEME: {{theme.Name}}. {{theme.Description}}
            OTHER THEMES (reported in their own subsections; do not cover them here): {{(otherThemes.Length == 0 ? "none" : otherThemes)}}

            REFERENCE LIST FOR THIS THEME (cite ONLY these numbers):
            {{evidence.ReferenceLines(theme.Studies)}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(themeEvidence, "the evidence of the studies in this theme: codes, verified findings and excerpts")}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraph{{(paragraphs == 1 ? "" : "s")}} of synthesis, not a list of summaries: group studies that report the same thing, compare and contrast them, and name agreements, tensions and gaps within the theme. Separate paragraphs with a blank line.
            2. Cite every one of these studies at least once: {{studyList}}. A study may be cited together with others when they report the same finding ("... [3, 7, 12]").
            3. Every sentence that states a finding, comparison or claim from the literature ends with an inline marker like [3] or [2, 5]. Attribute to a study only what its findings or excerpts above actually say; prefer the verified findings. Do not invent numbers, statistics or venue names.
            4. "heading" is a short subsection title for the theme (at most 10 words, no numbering).
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"heading":"...","text":"the subsection with inline [n] citations"}
            """;

        string text;
        string heading;
        try
        {
            using (LlmStage.Begin("theme-sections"))
            {
                var answer = await LlmJson.GetAsync<SectionAnswer>(chat, prompt, JsonMode(0.3), a =>
                {
                    if (string.IsNullOrWhiteSpace(a.Text)) return "text must not be empty.";
                    var cited = ThematicSynthesis.CitedIn(a.Text);
                    if (cited.Count == 0) return "the text must carry inline [n] citations.";
                    var outside = cited.Where(r => r < 1 || r > referenceCount).ToList();
                    return outside.Count > 0 ? $"citation numbers {string.Join(", ", outside)} are not in the reference list." : null;
                });
                text = MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
                heading = string.IsNullOrWhiteSpace(answer.Heading) ? theme.Name : answer.Heading.Trim().TrimEnd('.');
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Theme {Theme} could not be written: {Message}", theme.Id, SanitizeLogMessage(ex.Message));
            coverage.FillPass = "subsection could not be written";
            coverage.NotCited = theme.Studies.ToList();
            return (null, coverage);
        }

        coverage.CitedAfterWriting = ThematicSynthesis.CitedIn(text).Intersect(theme.Studies).ToList();
        var missing = theme.Studies.Except(coverage.CitedAfterWriting).ToList();
        if (missing.Count > 0)
        {
            string missingEvidence = string.Join("\n", missing.Select(r => evidence.SourceBlock(r, evidenceTheme, 2)));
            string fillPrompt = $$"""
                You are revising one theme subsection of a systematic review so that it covers every study of the theme. The studies below belong to this theme but are not yet cited in it.

                THEME: {{theme.Name}}. {{theme.Description}}

                REFERENCE LIST FOR THIS THEME (cite ONLY these numbers):
                {{evidence.ReferenceLines(theme.Studies)}}

                CURRENT SUBSECTION:
                {{text}}

                {{PromptSafety.DataOnlyNotice}}

                {{PromptSafety.Wrap(missingEvidence, "evidence of the studies not yet cited")}}

                TASK: Integrate the studies {{string.Join(", ", missing.Select(r => $"[{r}]"))}} where their evidence fits: add them to the comparisons that already exist, or add sentences that relate them to the studies already discussed. Attribute to them only what their evidence above says. Keep every existing citation. If the evidence of a study does not support any statement about this theme, leave it out rather than inventing one.
                {{ProseCleaner.PlainProseRule}}
                Respond ONLY with a valid minified JSON object:
                {"text":"the revised subsection with inline [n] citations"}
                """;
            try
            {
                using (LlmStage.Begin("coverage-fill"))
                {
                    var answer = await LlmJson.GetAsync<TextAnswer>(chat, fillPrompt, JsonMode(0.2), a =>
                        string.IsNullOrWhiteSpace(a.Text) ? "text must not be empty." : null);
                    string revised = MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
                    string? problem = ThematicSynthesis.RevisionProblem(text, revised, referenceCount);
                    if (problem == null)
                    {
                        int added = ThematicSynthesis.CitedIn(revised).Intersect(missing).Count();
                        coverage.FillPass = $"added {added} of {missing.Count} uncited studies";
                        text = revised;
                    }
                    else coverage.FillPass = $"revision rejected: {problem}";
                }
            }
            catch (Exception ex)
            {
                coverage.FillPass = $"fill pass failed ({ex.GetType().Name})";
            }
        }
        coverage.CitedFinal = ThematicSynthesis.CitedIn(text).Intersect(theme.Studies).ToList();
        coverage.NotCited = theme.Studies.Except(coverage.CitedFinal).ToList();
        return (new SynthesisSection { Field = field, ThemeId = theme.Id, Heading = heading, Text = text }, coverage);
    }

    private async Task<string> WriteDiscussionAsync(IChatCompletionService chat, string query, string goal,
        IReadOnlyList<SynthesisSection> sections, ReviewEvidence evidence, int referenceCount)
    {
        int paragraphs = Math.Clamp(2 + sections.Count / 2, 3, 6);
        string synthesis = string.Join("\n\n", sections.Select(s => $"### {s.Heading}\n{s.Text}"));
        string prompt = $$"""
            You are writing the Discussion section (PRISMA 2020 Item 23a) of a systematic literature review whose results are reported theme by theme below.

            REVIEW OBJECTIVE: "{{goal}}"
            PRIMARY TOPIC: "{{query}}"
            {{PromptSafety.ReviewerInputNotice}}

            REFERENCE LIST (cite ONLY these numbers):
            {{evidence.ReferenceLines()}}

            FACTS ABOUT THE EVIDENCE BASE (from the data extraction; use them for the limitations, do not change them):
            {{evidence.BaseFacts}}

            RESULTS BY THEME:
            {{synthesis}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraphs, separated by a blank line: (a) what the themes together say about the objective, (b) where the themes connect, reinforce or contradict each other, (c) gaps the literature leaves open and directions for research, (d) implications for practice, (e) the limitations of the evidence base and of this review (automated screening and synthesis by a language model).
            2. Interpret, do not repeat the results. Ground every claim in the results above; every sentence that attributes something to the literature ends with an inline marker such as [3] or [2, 5], using the same studies the results cite for it.
            3. Do not invent numbers, statistics or studies.
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"text":"the discussion with inline [n] citations"}
            """;
        try
        {
            var answer = await LlmJson.GetAsync<TextAnswer>(chat, prompt, JsonMode(0.3), a =>
            {
                if (string.IsNullOrWhiteSpace(a.Text)) return "text must not be empty.";
                var outside = ThematicSynthesis.CitedIn(a.Text).Where(r => r < 1 || r > referenceCount).ToList();
                return outside.Count > 0 ? $"citation numbers {string.Join(", ", outside)} are not in the reference list." : null;
            });
            return MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning("Discussion could not be written: {Message}", SanitizeLogMessage(ex.Message));
            return "";
        }
    }

    /// <summary>
    /// One critique of all subsections and the discussion, then a revision of each section that received
    /// medium or high comments. A revision that drops a citation the section had (and so lowers coverage) or
    /// cites outside the reference list is rejected, and the original is kept.
    /// </summary>
    private async Task<PeerReviewLog> PeerReviewSectionsAsync(IChatCompletionService chat, ThematicResult result,
        ReviewEvidence evidence, int referenceCount)
    {
        string AllSynthesis() => string.Join("\n\n", result.Sections.Select(s => $"### {s.Heading}\n{s.Text}"));
        var log = new PeerReviewLog
        {
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            SynthesisBefore = AllSynthesis(),
            DiscussionBefore = result.Discussion,
            Verdict = "No changes applied",
        };
        var ids = result.Sections.Select(s => s.Field).Append("discussion").ToList();
        string sectionsText = string.Join("\n\n", result.Sections.Select(s => $"[{s.Field}] {s.Heading}\n{s.Text}"))
                              + $"\n\n[discussion] Discussion\n{result.Discussion}";

        string critiquePrompt = $$"""
            You are a strict but constructive peer reviewer for a systematic literature review. Review each section below (theme subsections of the results, and the discussion) for: (a) depth: does it synthesise and compare rather than list, (b) whether each specific claim carries an inline [n] citation, (c) over-claiming beyond what a study could show, (d) coherence and overlap between sections, (e) whether the discussion interprets rather than repeats.

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
            var answer = await LlmJson.GetAsync<SectionCritiqueAnswer>(chat, critiquePrompt, JsonMode(0.2), a =>
                a.Comments == null ? "comments must be a list (it may be empty)." :
                a.Comments.Any(c => !ids.Contains(c.Section.Trim(), StringComparer.OrdinalIgnoreCase)) ? $"each comment's section must be one of: {string.Join(", ", ids)}." : null);
            log.Comments = answer.Comments!.Select(c => new PeerReviewComment(c.Section.Trim().ToLowerInvariant(), c.Severity, c.Issue, c.Suggestion)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Peer-review critique skipped: {Message}", SanitizeLogMessage(ex.Message));
        }

        var actionable = log.Comments
            .Where(c => !c.Severity.Equals("low", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Section).ToList();
        if (actionable.Count == 0)
        {
            log.SynthesisAfter = log.SynthesisBefore;
            log.DiscussionAfter = log.DiscussionBefore;
            log.Verdict = log.Comments.Count == 0 ? "No revisions needed - reviewer found no actionable issues" : "Only low-severity comments; no revisions applied";
            return log;
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        var revisions = actionable.Select(group => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try
            {
                var section = result.Sections.FirstOrDefault(s => s.Field == group.Key);
                string before = section?.Text ?? result.Discussion;
                string feedback = string.Join("\n", group.Select(c => $"- [{c.Severity}] {c.Issue} -> {c.Suggestion}"));
                string revisePrompt = $$"""
                    You are the author revising one section of a systematic review in response to peer-review feedback. Apply the feedback. Keep every existing inline [n] citation, cite ONLY numbers from the reference list, and do not invent claims, numbers or studies.

                    REFERENCE LIST (valid citation numbers):
                    {{evidence.ReferenceLines()}}

                    PEER-REVIEW FEEDBACK:
                    {{feedback}}

                    CURRENT SECTION ({{(section == null ? "Discussion" : section.Heading)}}):
                    {{before}}

                    {{ProseCleaner.PlainProseRule}}
                    Respond ONLY with a valid minified JSON object:
                    {"text":"the revised section with inline [n] citations"}
                    """;
                try
                {
                    var answer = await LlmJson.GetAsync<TextAnswer>(chat, revisePrompt, JsonMode(0.3), a =>
                        string.IsNullOrWhiteSpace(a.Text) ? "text must not be empty." : null);
                    string after = MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
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
            var section = result.Sections.FirstOrDefault(s => s.Field == revision.Section);
            if (section != null) section.Text = revision.After;
            else result.Discussion = revision.After;
        }
        log.SectionRevisions = log.SectionRevisions.OrderBy(r => ids.IndexOf(r.Section)).ToList();
        log.SynthesisAfter = AllSynthesis();
        log.DiscussionAfter = result.Discussion;
        int applied = log.SectionRevisions.Count(r => r.Applied);
        log.Verdict = $"Revisions applied to {applied} of {log.SectionRevisions.Count} section(s) with medium or high comments";
        return log;
    }

    // ─── Citation repair (in Verification/CitationRepairer.cs, shared by every kind of review) ───────────────

    /// <summary>Removes one reference number from the citation markers of a sentence (see <see cref="CitationRepairer.RemoveReference"/>).</summary>
    public static string RemoveReference(string sentence, int reference) => CitationRepairer.RemoveReference(sentence, reference);

    /// <summary>Should the repair pass look at this verdict? (see <see cref="CitationRepairer.NeedsRepair"/>).</summary>
    public static bool NeedsRepair(CitationSupportResult r) => CitationRepairer.NeedsRepair(r);
}

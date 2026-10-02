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
    private sealed class RepairAnswer { public List<RepairItem>? Repairs { get; set; } }
    private sealed class RepairItem { public int Id { get; set; } public string Action { get; set; } = ""; public string? Sentence { get; set; } public string? Reason { get; set; } }

    /// <summary>One sentence changed (or left alone) by the citation repair pass, as written to citation-audit.json.</summary>
    public record CitationRepair(string Field, string Before, string After, string Action, IReadOnlyList<int> FlaggedReferences, string InitialVerdicts, string Note);

    /// <summary>
    /// Runs coding, codebook, optional second coding, theme subsections with the coverage fill pass, the
    /// discussion and the per-section peer review. Returns a result without sections when the synthesis has
    /// to fall back to the single-pass writer (the reason is in <see cref="ThematicCodebook.FallbackReason"/>).
    /// </summary>
    private async Task<ThematicResult> RunThematicSynthesisAsync(
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
            var (c, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, goal, papers, extractions,
                Synthesis.CodingBatchSize, Llm.ScreeningParallelism);
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
        var apa = state.SynthesizedRecords.ToDictionary(r => r.ReferenceNumber, r => r.ApaCitation);
        var byRef = papers.ToDictionary(p => p.ReferenceNumber);
        var extractionByRef = extractions.ToDictionary(e => e.ReferenceNumber);
        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        var work = book.Themes.Select((theme, index) => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try { return await WriteThemeAsync(chat, goal, book, theme, index + 1, apa, byRef, extractionByRef, referenceCount); }
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
        using (LlmStage.Begin("discussion"))
            result.Discussion = await WriteDiscussionAsync(chat, query, goal, result.Sections, extractions, apa, referenceCount);

        // 6. Peer review, per section, with the same coverage guard as the fill pass
        using (LlmStage.Begin("automated-peer-review"))
            result.PeerReview = await PeerReviewSectionsAsync(chat, result, apa, referenceCount);

        return result;
    }

    /// <summary>The reference-list lines of the given studies.</summary>
    private static string ReferenceLines(IEnumerable<int> refs, IReadOnlyDictionary<int, string> apa) =>
        string.Join("\n", refs.OrderBy(r => r).Select(r => $"[{r}] - {(apa.TryGetValue(r, out var c) ? c : "(citation unavailable)")}"));

    /// <summary>What the writer gets for one study of a theme: codes, verified findings and a few excerpts.</summary>
    private static string StudyEvidence(int reference, Theme? theme, ThematicCodebook book,
        IReadOnlyDictionary<int, ReferencedPaper> papers, IReadOnlyDictionary<int, StudyExtraction> extractions, int excerpts)
    {
        var sb = new StringBuilder();
        papers.TryGetValue(reference, out var paper);
        extractions.TryGetValue(reference, out var e);
        sb.AppendLine($"STUDY [{reference}] {paper?.Title}");
        if (e != null && e.Error == null)
            sb.AppendLine($"Study type: {e.StudyType}; evidence: {e.EvidenceBasis}; method: {e.Method.Value}");
        var codes = book.Codes.Where(c => c.Reference == reference && (theme == null || theme.CodeIds.Contains(c.Id))).ToList();
        if (codes.Count > 0) sb.AppendLine($"Codes{(theme == null ? "" : " in this theme")}: {string.Join("; ", codes.Select(c => c.Label))}");
        var findings = ThematicSynthesis.VerifiedFindings(e);
        foreach (var (id, f) in findings) sb.AppendLine($"Finding {id}: {f.Value} (verbatim: \"{f.Quote}\")");
        if (findings.Count == 0 && paper != null && !string.IsNullOrWhiteSpace(paper.Abstract))
            sb.AppendLine($"Abstract: {paper.Abstract.Trim()}");
        if (paper != null && excerpts > 0)
        {
            var terms = TextRelevance.Terms($"{theme?.Name} {theme?.Description} {string.Join(" ", codes.Select(c => c.Label))}");
            foreach (var chunk in GroundingContextBuilder.SelectChunks(paper.Chunks, terms, excerpts))
                sb.AppendLine($"[{reference}, page {chunk.PageNumber}]: {chunk.Text}");
        }
        return sb.ToString();
    }

    private async Task<(SynthesisSection? Section, ThemeCoverage Coverage)> WriteThemeAsync(
        IChatCompletionService chat, string goal, ThematicCodebook book, Theme theme, int number,
        IReadOnlyDictionary<int, string> apa, IReadOnlyDictionary<int, ReferencedPaper> papers,
        IReadOnlyDictionary<int, StudyExtraction> extractions, int referenceCount)
    {
        string field = $"synthesis-{number}";
        var coverage = new ThemeCoverage { ThemeId = theme.Id, Field = field, Studies = theme.Studies.ToList() };
        int excerpts = theme.Studies.Count <= 12 ? 2 : 1;
        string evidence = string.Join("\n", theme.Studies.Select(r => StudyEvidence(r, theme, book, papers, extractions, excerpts)));
        string otherThemes = string.Join("; ", book.Themes.Where(t => t.Id != theme.Id).Select(t => t.Name));
        int paragraphs = ThematicSynthesis.ParagraphsFor(theme.Studies.Count);
        string studyList = string.Join(", ", theme.Studies.Select(r => $"[{r}]"));

        string prompt = $$"""
            You are writing one subsection of the Results & Synthesis section (PRISMA 2020 Item 20a) of a systematic literature review. The review used thematic synthesis; this subsection reports one theme.

            REVIEW OBJECTIVE: "{{goal}}"
            THEME: {{theme.Name}}. {{theme.Description}}
            OTHER THEMES (reported in their own subsections; do not cover them here): {{(otherThemes.Length == 0 ? "none" : otherThemes)}}

            REFERENCE LIST FOR THIS THEME (cite ONLY these numbers):
            {{ReferenceLines(theme.Studies, apa)}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(evidence, "the evidence of the studies in this theme: codes, verified findings and excerpts")}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraph{{(paragraphs == 1 ? "" : "s")}} of synthesis, not a list of summaries: group studies that report the same thing, compare and contrast them, and name agreements, tensions and gaps within the theme. Separate paragraphs with a blank line.
            2. Cite every one of these studies at least once: {{studyList}}. A study may be cited together with others when they report the same finding ("... [3, 7, 12]").
            3. Every sentence that states a finding, comparison or claim from the literature ends with an inline marker like [3] or [2, 5]. Attribute to a study only what its findings or excerpts above actually say; prefer the verified findings. Do not invent numbers, statistics or venue names.
            4. "heading" is a short subsection title for the theme (at most 10 words, no numbering).
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
            string missingEvidence = string.Join("\n", missing.Select(r => StudyEvidence(r, theme, book, papers, extractions, 2)));
            string fillPrompt = $$"""
                You are revising one theme subsection of a systematic review so that it covers every study of the theme. The studies below belong to this theme but are not yet cited in it.

                THEME: {{theme.Name}}. {{theme.Description}}

                REFERENCE LIST FOR THIS THEME (cite ONLY these numbers):
                {{ReferenceLines(theme.Studies, apa)}}

                CURRENT SUBSECTION:
                {{text}}

                {{PromptSafety.DataOnlyNotice}}

                {{PromptSafety.Wrap(missingEvidence, "evidence of the studies not yet cited")}}

                TASK: Integrate the studies {{string.Join(", ", missing.Select(r => $"[{r}]"))}} where their evidence fits: add them to the comparisons that already exist, or add sentences that relate them to the studies already discussed. Attribute to them only what their evidence above says. Keep every existing citation. If the evidence of a study does not support any statement about this theme, leave it out rather than inventing one.
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

    /// <summary>Facts about the evidence base for the limitations paragraph, from the extraction (not the model).</summary>
    private static string EvidenceBaseFacts(IReadOnlyList<StudyExtraction> extractions)
    {
        if (extractions.Count == 0) return "(no extraction data)";
        int fullText = extractions.Count(e => e.EvidenceBasis == "full text");
        int notEmpirical = extractions.Count(e => e.AppraisalCategory == "not_empirical");
        var types = extractions.Where(e => e.Error == null && !string.IsNullOrWhiteSpace(e.StudyType))
            .GroupBy(e => e.StudyType.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => $"{g.Key} ({g.Count()})");
        return $"{extractions.Count} studies; full text was read for {fullText} and only the abstract for {extractions.Count - fullText}; " +
               $"{notEmpirical} are not empirical (position papers, system descriptions or reviews). Most common study types: {string.Join(", ", types)}.";
    }

    private async Task<string> WriteDiscussionAsync(IChatCompletionService chat, string query, string goal,
        IReadOnlyList<SynthesisSection> sections, IReadOnlyList<StudyExtraction> extractions,
        IReadOnlyDictionary<int, string> apa, int referenceCount)
    {
        int paragraphs = Math.Clamp(2 + sections.Count / 2, 3, 6);
        string synthesis = string.Join("\n\n", sections.Select(s => $"### {s.Heading}\n{s.Text}"));
        string prompt = $$"""
            You are writing the Discussion section (PRISMA 2020 Item 23a) of a systematic literature review whose results are reported theme by theme below.

            REVIEW OBJECTIVE: "{{goal}}"
            PRIMARY TOPIC: "{{query}}"

            REFERENCE LIST (cite ONLY these numbers):
            {{ReferenceLines(apa.Keys, apa)}}

            FACTS ABOUT THE EVIDENCE BASE (from the data extraction; use them for the limitations, do not change them):
            {{EvidenceBaseFacts(extractions)}}

            RESULTS BY THEME:
            {{synthesis}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraphs, separated by a blank line: (a) what the themes together say about the objective, (b) where the themes connect, reinforce or contradict each other, (c) gaps the literature leaves open and directions for research, (d) implications for practice, (e) the limitations of the evidence base and of this review (automated screening and synthesis by a language model).
            2. Interpret, do not repeat the results. Ground every claim in the results above; every sentence that attributes something to the literature ends with an inline marker such as [3] or [2, 5], using the same studies the results cite for it.
            3. Do not invent numbers, statistics or studies.
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
        IReadOnlyDictionary<int, string> apa, int referenceCount)
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
            {{ReferenceLines(apa.Keys, apa)}}

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
                    {{ReferenceLines(apa.Keys, apa)}}

                    PEER-REVIEW FEEDBACK:
                    {{feedback}}

                    CURRENT SECTION ({{(section == null ? "Discussion" : section.Heading)}}):
                    {{before}}

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

    // ─── Citation repair ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Removes one reference number from the citation markers of a sentence (and an emptied marker).</summary>
    public static string RemoveReference(string sentence, int reference)
    {
        string cleaned = Regex.Replace(sentence, @"\s*\[(\d+(?:\s*[,–-]\s*\d+)*)\]", m =>
        {
            var refs = CitationSupportChecker.ReferencesIn(m.Value).Where(r => r != reference).ToList();
            if (refs.Count == 0) return "";
            string lead = m.Value.Length > 0 && char.IsWhiteSpace(m.Value[0]) ? " " : "";
            return $"{lead}[{string.Join(", ", refs)}]";
        });
        return Regex.Replace(cleaned, @"\s+([.,;:!?])", "$1");
    }

    /// <summary>Should the repair pass look at this verdict? Not supported, or a positive verdict whose quote was not found.</summary>
    public static bool NeedsRepair(CitationSupportResult r) =>
        r.Verdict == CitationSupportChecker.NotSupported ||
        (r.Verdict == CitationSupportChecker.Unverifiable && r.EvidenceBasis != "none" && r.Reason.StartsWith("Model said", StringComparison.Ordinal));

    /// <summary>
    /// For every sentence with a rejected citation: one call per section asks the model to rewrite the sentence
    /// from the cited paper's evidence, to drop that citation, or to delete the sentence. A rewrite may only keep
    /// or remove the sentence's own citations (never add new ones). The changed sentences are checked again;
    /// unchanged sentences keep their verdicts. Returns the new texts, the merged results and the repair log.
    /// </summary>
    private async Task<(List<(string Field, string Text)> Fields, List<CitationSupportResult> Results, List<CitationRepair> Repairs)> RepairCitationsAsync(
        IChatCompletionService chat, List<(string Field, string Text)> fields, List<CitationSupportResult> results,
        IReadOnlyList<ReferencedPaper> papers, IReadOnlyDictionary<int, IReadOnlyList<string>> findings, int referenceCount)
    {
        var repairs = new List<CitationRepair>();
        var flagged = results.Where(NeedsRepair).ToList();
        if (flagged.Count == 0) return (fields, results, repairs);

        var byRef = papers.ToDictionary(p => p.ReferenceNumber);
        string Evidence(int reference, string sentence)
        {
            if (!byRef.TryGetValue(reference, out var p)) return "(no text)";
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(p.Abstract)) sb.AppendLine($"Abstract: {p.Abstract.Trim()}");
            if (findings.TryGetValue(reference, out var quotes)) foreach (var q in quotes) sb.AppendLine($"Verified quote: {q}");
            foreach (var c in GroundingContextBuilder.SelectChunks(p.Chunks, TextRelevance.Terms(sentence), 4)) sb.AppendLine($"[page {c.PageNumber}]: {c.Text}");
            return sb.ToString();
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        var work = flagged.GroupBy(r => r.Field).Select(group => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try
            {
                var items = group.GroupBy(r => r.SentenceIndex).OrderBy(g => g.Key).Select((g, i) => (Id: i + 1, Sentence: g.First().Sentence, Checks: g.ToList())).ToList();
                var data = new StringBuilder();
                foreach (var item in items)
                {
                    data.AppendLine($"ITEM {item.Id}: {item.Sentence}");
                    foreach (var check in item.Checks)
                    {
                        data.AppendLine($"  Citation [{check.Reference}] was judged {check.Verdict.Replace('_', ' ')}: {check.Reason}");
                        data.AppendLine($"  Evidence from [{check.Reference}]:\n{Evidence(check.Reference, item.Sentence)}");
                    }
                    data.AppendLine();
                }
                string prompt = $$"""
                    You are correcting citations in a systematic literature review. An automated check found that the sentences below say something about a cited paper that its text does not support. For each item, choose one action:
                    - "rewrite": return the sentence rewritten so that what it attributes to each flagged paper is what the evidence below says. Keep the sentence's role in the paragraph, and keep its citation markers (you may remove one, never add a new number).
                    - "drop_citation": the sentence is fine without the flagged paper; the flagged citation will be removed.
                    - "delete": the sentence cannot be supported and should be removed.

                    {{PromptSafety.DataOnlyNotice}}

                    {{PromptSafety.Wrap(data.ToString(), "the flagged sentences and the cited papers' evidence")}}

                    Respond ONLY with a minified JSON object:
                    {"repairs":[{"id":1,"action":"rewrite","sentence":"the corrected sentence with its [n] markers","reason":"one short sentence"}]}
                    """;
                try
                {
                    var answer = await LlmJson.GetAsync<RepairAnswer>(chat, prompt, JsonMode(0.0), a =>
                        a.Repairs == null ? "repairs must be a list." :
                        a.Repairs.Any(r => !LlmJson.OneOf(r.Action, "rewrite", "drop_citation", "delete")) ? "each action must be rewrite, drop_citation or delete." : null);
                    return (group.Key, items, answer.Repairs!, (string?)null);
                }
                catch (Exception ex)
                {
                    return (group.Key, items, new List<RepairItem>(), $"Repair call failed ({ex.GetType().Name}).");
                }
            }
            finally { throttle.Release(); }
        })).ToList();

        var texts = fields.ToDictionary(f => f.Field, f => f.Text);
        foreach (var task in work)
        {
            var (field, items, answers, error) = await task;
            foreach (var item in items)
            {
                var flaggedRefs = item.Checks.Select(c => c.Reference).Distinct().ToList();
                string verdicts = string.Join(", ", item.Checks.Select(c => $"[{c.Reference}] {c.Verdict}"));
                var answer = answers.FirstOrDefault(a => a.Id == item.Id);
                if (answer == null)
                {
                    repairs.Add(new CitationRepair(field, item.Sentence, item.Sentence, "none", flaggedRefs, verdicts, error ?? "No repair was proposed; the sentence is unchanged."));
                    continue;
                }
                string action = answer.Action.Trim().ToLowerInvariant();
                string replacement;
                if (action == "delete") replacement = "";
                else if (action == "drop_citation") replacement = flaggedRefs.Aggregate(item.Sentence, RemoveReference);
                else
                {
                    replacement = MethodsSectionWriter.StripMarkdownEmphasis((answer.Sentence ?? "").Trim());
                    var original = CitationSupportChecker.ReferencesIn(item.Sentence).ToHashSet();
                    var added = CitationSupportChecker.ReferencesIn(replacement).Where(r => !original.Contains(r) || r > referenceCount).ToList();
                    if (replacement.Length == 0 || added.Count > 0)
                    {
                        repairs.Add(new CitationRepair(field, item.Sentence, item.Sentence, "rejected", flaggedRefs, verdicts,
                            replacement.Length == 0 ? "The rewrite was empty; the sentence is unchanged." : $"The rewrite added citations {string.Join(", ", added)}; the sentence is unchanged."));
                        continue;
                    }
                }

                string current = texts[field];
                int at = current.IndexOf(item.Sentence, StringComparison.Ordinal);
                if (at < 0)
                {
                    repairs.Add(new CitationRepair(field, item.Sentence, item.Sentence, "not applied", flaggedRefs, verdicts, "The sentence was not found in the section text."));
                    continue;
                }
                string updated = current[..at] + replacement + current[(at + item.Sentence.Length)..];
                if (replacement.Length == 0) updated = Regex.Replace(updated, @"[ \t]{2,}", " ");
                texts[field] = updated.Trim();
                repairs.Add(new CitationRepair(field, item.Sentence, replacement, action, flaggedRefs, verdicts, (answer.Reason ?? "").Trim()));
            }
        }

        var newFields = fields.Select(f => (f.Field, texts[f.Field])).ToList();

        // Check again only what changed; unchanged sentences keep their verdicts (with their new position).
        var previous = results.GroupBy(r => (r.Field, r.Sentence, r.Reference)).ToDictionary(g => g.Key, g => g.First());
        var cited = newFields.SelectMany(f => CitationSupportChecker.ExtractCitedSentences(f.Item2, f.Field)).ToList();
        var merged = new List<CitationSupportResult>();
        var toCheck = new List<CitedSentence>();
        foreach (var s in cited)
        {
            var unchecked_ = new List<int>();
            foreach (var r in s.References)
            {
                if (previous.TryGetValue((s.Field, s.Sentence, r), out var old))
                {
                    old.SentenceIndex = s.SentenceIndex;
                    merged.Add(old);
                }
                else unchecked_.Add(r);
            }
            if (unchecked_.Count > 0) toCheck.Add(new CitedSentence(s.Field, s.SentenceIndex, s.Sentence, unchecked_));
        }
        if (toCheck.Count > 0)
        {
            using (LlmStage.Begin("citation-check"))
                merged.AddRange(await CitationSupportChecker.CheckAsync(chat, toCheck, papers, verifiedFindings: findings, parallelism: Llm.ScreeningParallelism));
        }
        var ordered = merged.OrderBy(r => r.Field).ThenBy(r => r.SentenceIndex).ThenBy(r => r.Reference).ToList();
        return (newFields, ordered, repairs);
    }
}

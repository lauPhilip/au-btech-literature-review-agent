using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The grounded outline, and one cited subsection per theme with its fill pass.</summary>
public static partial class ReviewWriter
{
    private sealed class OutlineAnswer { public List<OutlineTheme>? Themes { get; set; } }
    private sealed class OutlineTheme { public string Theme { get; set; } = ""; public List<OutlineClaim>? Claims { get; set; } }
    private sealed class OutlineClaim { public string Text { get; set; } = ""; public List<int>? Refs { get; set; } }

    /// <summary>
    /// The grounded outline, written before any prose (STORM-style): a few themes and, per theme, the specific claims
    /// the sources' excerpts support with the reference numbers that back each claim, so the prose is written
    /// against a claim map rather than in one uncontrolled pass. Kept as grounded-outline.txt. Returns an empty
    /// string (never throws) on any failure, so the writing grounds itself directly on the excerpts.
    /// </summary>
    public static async Task<string> WriteOutlineAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        string sourceExcerpts, string referenceLines, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(sourceExcerpts)) return string.Empty;

        var outlinePrompt = $$"""
            You are preparing a grounded synthesis outline for {{profile.Review}}: before writing prose, map out the specific claims the literature supports and which sources back each claim.

            REVIEW OBJECTIVE: "{{goal}}"
            PRIMARY TOPIC: "{{topic}}"
            {{PromptSafety.ReviewerInputNotice}}

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceLines}}

            {{PromptSafety.DataOnlyNotice}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{PromptSafety.Wrap(sourceExcerpts, profile.SourceTexts)}}

            TASK: Identify 3 to 6 distinct themes that emerge across the included sources. For each theme, list 1-3 concrete, specific claims that the grounded chunks actually support, and for each claim list which reference numbers from the list above support it. Only use reference numbers that appear in the list above. If the grounded chunks are too sparse to support a claim, omit it rather than inventing one.

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "themes": [ { "theme": "short theme label", "claims": [ { "text": "specific claim grounded in the sources", "refs": [1, 3] } ] } ] }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<OutlineAnswer>(chat, outlinePrompt, LlmJson.JsonMode(0.2), a =>
                a.Themes == null || a.Themes.Count == 0 ? "themes must be a non-empty list." :
                a.Themes.Any(t => t.Claims == null) ? "every theme needs a claims list." : null);

            var sb = new System.Text.StringBuilder();
            foreach (var theme in answer.Themes!.Where(t => !string.IsNullOrWhiteSpace(t.Theme)))
            {
                sb.AppendLine($"- Theme: {theme.Theme}");
                foreach (var claim in theme.Claims!.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
                    sb.AppendLine($"    * {claim.Text} {string.Join("", (claim.Refs ?? new()).Select(r => $"[{r}]"))}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Outline stage skipped: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// The prompt rule that writes each claim so its citation can back it (issue #248). The citation check judges the
    /// part of a sentence attributed to each source, and calls it partly supported when that part adds a number, a
    /// qualifier, a scope, a causal link or a comparison the source does not state; a claim several sources are
    /// cited for together must be in each of them. Those are the claims the live runs lost after the repair.
    /// </summary>
    public static string ClaimPerCitationRule(WritingProfile profile) =>
        $"Write so that each citation backs exactly what it is attached to: put a marker right after the claim it supports, one finding per citation, and cite several {profile.Sources} together only when each of them states the whole claim. " +
        $"When {profile.Sources} differ, give each its own claim and marker (\"one reports X [1], while another reports Y [2]\"); never attribute a comparison, ranking or trend across {profile.Sources} to a {profile.Source} that does not make it itself. " +
        $"Use no number, count or proportion (\"three approaches\", \"most {profile.Sources}\", \"four of six\") unless it is in the evidence or the facts above; a count of this review's own {profile.Sources} comes from those facts and takes no citation. " +
        $"Do not add a qualifier (\"always\", \"all\", \"widely\"), a scope or a causal link that the {profile.Source} does not state. " +
        // Issue #674: "Devgan's systematic review ... [3]" and "Li et al. propose ... [5]" named and cited one source in one clause.
        $"Name a {profile.Source} by its marker or cite it after the claim, never both in one clause: write \"[4] identifies X\" or \"X [4]\", not \"[4] identifies X [4]\" or \"Li et al. identify X [4]\"." +
        " " + CitationLoadRule(profile);

    /// <summary>
    /// Writes one subsection per theme, at most <paramref name="parallelism"/> at a time, assembled in theme order.
    /// A theme that cannot be written is left out and its coverage says so.
    /// </summary>
    public static async Task<(List<SynthesisSection> Sections, List<ThemeCoverage> Coverage)> WriteThemesAsync(
        IChatCompletionService chat, WritingProfile profile, string goal, ReviewEvidence evidence, int referenceCount,
        int parallelism, Action<int, int>? written = null, Action<string>? warn = null)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, parallelism));
        int done = 0;
        var work = evidence.Themes.Select((theme, index) => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try
            {
                var result = await WriteThemeAsync(chat, profile, goal, evidence, theme, index + 1, referenceCount, warn);
                written?.Invoke(Interlocked.Increment(ref done), evidence.Themes.Count);
                return result;
            }
            finally { throttle.Release(); }
        })).ToList();

        var sections = new List<SynthesisSection>();
        var coverage = new List<ThemeCoverage>();
        foreach (var task in work)
        {
            var (section, themeCoverage) = await task;
            coverage.Add(themeCoverage);
            if (section != null) sections.Add(section);
        }
        // The themes are written at the same time, so none sees the others; a claim another theme already reports is
        // pointed out afterwards, in theme order (issue #464).
        await RevisitRepeatsAsync(chat, profile, evidence, sections, coverage, referenceCount);
        return (sections, coverage);
    }

    /// <summary>One theme's subsection, with one fill pass for the theme's sources it does not cite yet.</summary>
    public static async Task<(SynthesisSection? Section, ThemeCoverage Coverage)> WriteThemeAsync(
        IChatCompletionService chat, WritingProfile profile, string goal, ReviewEvidence evidence, EvidenceTheme theme, int number,
        int referenceCount, Action<string>? warn = null)
    {
        string field = $"synthesis-{number}";
        var coverage = new ThemeCoverage { ThemeId = theme.Id, Field = field, Studies = theme.Sources.ToList() };
        int excerpts = theme.Sources.Count <= 12 ? 2 : 1;
        string themeEvidence = string.Join("\n", theme.Sources.Select(r => evidence.SourceBlock(r, theme, excerpts, profile.LeanEvidence)));
        string otherThemes = string.Join("; ", evidence.Themes.Where(t => t.Id != theme.Id).Select(t => t.Name));
        int paragraphs = ThematicSynthesis.ParagraphsFor(theme.Sources.Count);
        string s = profile.Source, ss = profile.Sources;
        // The theme's own counts (issue #146), when the module gives them; without them the prompt is as before.
        string themeFacts = theme.Facts.Length == 0 ? ""
            : $"\nFACTS ABOUT THE {ss.ToUpperInvariant()} IN THIS THEME ({profile.BaseFactsFrom}; use them whenever you say how these {ss} were designed, and do not change them): {theme.Facts}";
        // The research questions the theme answers (issue #124); without them the prompt is as before.
        string themeQuestion = theme.Question.Length == 0 ? "" : $"\nTHIS THEME ANSWERS: {theme.Question}";

        string prompt = $$"""
            You are writing one subsection of {{profile.ResultsSection}} of {{profile.Review}}. The review used thematic synthesis; this subsection reports one theme.

            REVIEW OBJECTIVE: "{{goal}}"
            {{PromptSafety.ReviewerInputNotice}}
            THEME: {{theme.Name}}. {{theme.Description}}{{themeQuestion}}
            OTHER THEMES (reported in their own subsections; do not cover them here): {{(otherThemes.Length == 0 ? "none" : otherThemes)}}{{themeFacts}}

            REFERENCE LIST FOR THIS THEME (cite ONLY these numbers):
            {{evidence.ReferenceLines(theme.Sources)}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(themeEvidence, $"the evidence of the {ss} in this theme: codes, verified findings and excerpts")}}{{EarlierThemesBlock(evidence, theme, profile)}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraph{{(paragraphs == 1 ? "" : "s")}} of synthesis, not a list of summaries: group {{ss}} that report the same thing, compare and contrast them, and name agreements, tensions and gaps within the theme. Separate paragraphs with a blank line.
            2. {{WhichSourcesRule(profile, theme.Sources)}}
            3. Every sentence that states a finding, comparison or claim from the literature ends with an inline marker like [3] or [2, 5]. Attribute to a {{s}} only what its findings or excerpts above actually say; prefer the verified findings. Do not invent numbers, statistics or venue names. {{VerifiedFigureRule(profile)}}
            4. "heading" is a short subsection title for the theme (at most 10 words, no numbering).
            5. {{ReadingBasisRule(profile)}}
            6. {{ClaimPerCitationRule(profile)}}
            7. {{RedThreadRule(profile, evidence, theme)}} Each entry of "leftOut" is {"ref":n,"reason":"why it bears on the theme only indirectly"}; leave the list empty when every {{s}} fits.
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"heading":"...","text":"the subsection with inline [n] citations","leftOut":[]}
            """;

        string text;
        string heading;
        try
        {
            using (LlmStage.Begin("theme-sections"))
            {
                void Note(string message) => (coverage.WritingNotes ??= new()).Add(message);
                var answer = await LlmJson.GetAsync<ThemeAnswer>(chat, prompt, LlmJson.JsonMode(0.3), WithSoftGuards<ThemeAnswer>(a =>
                {
                    if (string.IsNullOrWhiteSpace(a.Text)) return "text must not be empty.";
                    var cited = ThematicSynthesis.CitedIn(a.Text);
                    if (cited.Count == 0) return "the text must carry inline [n] citations.";
                    var outside = cited.Where(r => r < 1 || r > referenceCount).ToList();
                    if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
                    return LeftOutProblem(a.LeftOut, a.Text, theme.Sources) ?? CitationLoadSendBack(a.Text, "subsection", Note);
                }, "subsection", Note, 2, (a => ListingProblem(a.Text!, theme.Sources.Count, profile), "a list of one-source summaries"),
                    (a => UnquantifiedFindingProblem(a.Text!, evidence), "a result without its figures")), repairAttempts: 2);
                text = MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
                // A source left out of the theme is on file with its reason, and no fill pass puts it back (issue #675).
                if (answer.LeftOut is { Count: > 0 } left)
                    coverage.LeftOut = left.Select(l => new UncodedStudy(l.Ref, l.Reason!.Trim())).ToList();
                // The heading is always the theme's name from the codebook, set in code, so the subsection, the overview
                // and the evidence map name a theme the same way (issue #165). The model's own heading is not used.
                heading = theme.Name.Trim();
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Theme {theme.Id} could not be written: {ex.Message}");
            coverage.FillPass = "subsection could not be written";
            coverage.NotCited = theme.Sources.ToList();
            return (null, coverage);
        }

        coverage.CitedAfterWriting = ThematicSynthesis.CitedIn(text).Intersect(theme.Sources).ToList();
        var missing = theme.Sources.Except(coverage.CitedAfterWriting).Except((coverage.LeftOut ?? new()).Select(l => l.Reference)).ToList();
        if (missing.Count > 0 && theme.Sources.Count > CiteEveryUpTo)
            coverage.FillPass = $"not run: a theme of {theme.Sources.Count} {ss} is synthesised from its most direct {ss}, and its uncited {ss} are listed in the coverage";
        else if (missing.Count > 0)
        {
            string missingEvidence = string.Join("\n", missing.Select(r => evidence.SourceBlock(r, theme, 2, profile.LeanEvidence)));
            string fillPrompt = $$"""
                You are revising one theme subsection of {{profile.ReviewShort}} so that it covers every {{s}} of the theme. The {{ss}} below belong to this theme but are not yet cited in it.

                THEME: {{theme.Name}}. {{theme.Description}}{{themeFacts}}

                REFERENCE LIST FOR THIS THEME (cite ONLY these numbers):
                {{evidence.ReferenceLines(theme.Sources)}}

                CURRENT SUBSECTION:
                {{text}}

                {{PromptSafety.DataOnlyNotice}}

                {{PromptSafety.Wrap(missingEvidence, $"evidence of the {ss} not yet cited")}}

                TASK: Integrate the {{ss}} {{string.Join(", ", missing.Select(r => $"[{r}]"))}} where their evidence fits: add them to the comparisons that already exist, or add sentences that relate them to the {{ss}} already discussed. Attribute to them only what their evidence above says. Keep every existing citation. If the evidence of a {{s}} does not support any statement about this theme, leave it out rather than inventing one.
                {{ReadingBasisRule(profile)}}
                {{ClaimPerCitationRule(profile)}}
                {{ProseCleaner.PlainProseRule}}
                Respond ONLY with a valid minified JSON object:
                {"text":"the revised subsection with inline [n] citations"}
                """;
            try
            {
                using (LlmStage.Begin("coverage-fill"))
                {
                    string before = text;
                    var answer = await LlmJson.GetAsync<TextAnswer>(chat, fillPrompt, LlmJson.JsonMode(0.2), a =>
                        string.IsNullOrWhiteSpace(a.Text) ? "text must not be empty." : CitationLoadSendBack(a.Text, "fill pass", m => (coverage.WritingNotes ??= new()).Add(m), before));
                    string revised = MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
                    string? problem = ThematicSynthesis.RevisionProblem(text, revised, referenceCount);
                    if (problem == null)
                    {
                        int added = ThematicSynthesis.CitedIn(revised).Intersect(missing).Count();
                        coverage.FillPass = $"added {added} of {missing.Count} uncited {ss}";
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
        coverage.CitedFinal = ThematicSynthesis.CitedIn(text).Intersect(theme.Sources).ToList();
        coverage.NotCited = theme.Sources.Except(coverage.CitedFinal).ToList();
        return (new SynthesisSection { Field = field, ThemeId = theme.Id, Heading = heading, Text = text }, coverage);
    }
}

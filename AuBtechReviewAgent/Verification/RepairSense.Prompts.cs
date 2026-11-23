using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The two model calls of the sense check, each answer validated in code and kept in the review cache.
public static partial class RepairSense
{
    /// <summary>Part of the cache key of both prompts: raise it whenever either prompt changes.</summary>
    public const string PromptVersion = "repair-sense-v1";

    /// <summary>The cache kind the answers are kept under.</summary>
    public const string CacheKind = "repair-sense";

    private sealed class SenseAnswer { public bool? ReadsWell { get; set; } public string? Problem { get; set; } }
    private sealed class TextAnswer { public string? Text { get; set; } }

    /// <summary>
    /// One repaired paragraph: the sense check and, when it does not read well, one rewrite around its supported
    /// claims and a check of every citation in it. Returns the log entry, the rewrite (null when the paragraph stays as
    /// it is) and the rewrite's verdicts. A failed call leaves the paragraph as the repair left it, and says so.
    /// </summary>
    private static async Task<(RepairSenseCheck Entry, string? Rewrite, List<CitationSupportResult>? Fresh)> ParagraphAsync(
        IChatCompletionService chat, string field, string paragraph, IReadOnlyList<CitationRepair> changed, List<CitationSupportResult> checks,
        IReadOnlyList<ReferencedPaper> sources, IReadOnlyDictionary<int, IReadOnlyList<string>> verifiedQuotes, GroundingOptions options)
    {
        var changes = new StringBuilder();
        foreach (var c in changed)
            changes.AppendLine(c.After.Length == 0 ? $"- Removed: {c.Before}" : $"- Was: {c.Before}\n  Now: {c.After}");
        string sensePrompt = $$"""
            You are reading one paragraph of a {{options.Review}} after an automated citation repair changed some of its sentences. Judge only whether the paragraph still reads well: every changed sentence makes sense on its own and with the sentences around it, nothing promises more than follows (such as "three architectures" followed by two), nothing refers to a removed sentence, no sentence repeats or contradicts the one next to it, and the grammar is whole. Do not judge whether the citations are right; that is checked separately.

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap($"PARAGRAPH NOW:\n{paragraph}\n\nCHANGES THE REPAIR MADE:\n{changes}", "the repaired paragraph and its changes")}}

            Respond ONLY with a minified JSON object:
            {"readsWell":true,"problem":"one short sentence naming what does not read well, or an empty string"}
            """;
        SenseAnswer sense;
        try
        {
            using (LlmStage.Begin("citation-repair-sense"))
                sense = await CachedAsync<SenseAnswer>(chat, sensePrompt, options.Cache, a =>
                    a.ReadsWell == null ? "readsWell must be true or false." :
                    a.ReadsWell == false && string.IsNullOrWhiteSpace(a.Problem) ? "say in problem what does not read well." : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (new RepairSenseCheck(field, paragraph, paragraph, NotChecked, $"The sense check failed ({ex.GetType().Name}); the paragraph stays as the repair left it."), null, null);
        }
        if (sense.ReadsWell == true) return (new RepairSenseCheck(field, paragraph, paragraph, ReadsWell, ""), null, null);
        string problem = sense.Problem!.Trim();

        // The claims the rewrite may use: sentences whose every citation is supported, and the review's own uncited text.
        var sentences = CitationSupportChecker.SplitSentences(paragraph);
        bool Supported(string s) => CitationSupportChecker.ReferencesIn(s).All(r =>
            checks.Any(c => c.Field == field && c.Sentence == s && c.Reference == r && c.Verdict == CitationSupportChecker.Supported));
        var claims = sentences.Where(s => CitationSupportChecker.ReferencesIn(s).Count > 0 && Supported(s)).ToList();
        var own = sentences.Where(s => CitationSupportChecker.ReferencesIn(s).Count == 0).ToList();
        if (claims.Count == 0)
            return (new RepairSenseCheck(field, paragraph, paragraph, KeptBack, $"Does not read well ({problem}), but no sentence of it is supported throughout, so there is nothing to rewrite it around; it stays as the repair left it."), null, null);
        var allowed = claims.SelectMany(CitationSupportChecker.ReferencesIn).ToHashSet();
        var numbers = ReviewWriter.NumbersIn(paragraph);
        string rewritePrompt = $$"""
            You are rewriting one paragraph of a {{options.Review}} so that it reads well again after an automated citation repair. What does not read well: {{problem}}
            Write one paragraph from the supported claims below, as clear, connected prose. Keep each claim's meaning and its inline [n] markers exactly; do not add a claim, a number, a source or a citation, and do not widen a claim. You may reword or leave out the connecting text, and leave out a claim that cannot be fitted in.

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap($"SUPPORTED CLAIMS:\n{string.Join("\n", claims)}\n\nCONNECTING TEXT (the review's own words, no citation):\n{string.Join("\n", own)}", "the supported claims and connecting text")}}

            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a minified JSON object:
            {"text":"the paragraph with its inline [n] citations"}
            """;
        string rewrite;
        try
        {
            using (LlmStage.Begin("citation-repair-sense"))
                rewrite = MethodsSectionWriter.StripMarkdownEmphasis((await CachedAsync<TextAnswer>(chat, rewritePrompt, options.Cache, a =>
                {
                    if (string.IsNullOrWhiteSpace(a.Text)) return "text must not be empty.";
                    if (a.Text.Contains('\n')) return "write one paragraph, without line breaks.";
                    var markers = ThematicSynthesis.CitedIn(a.Text);
                    if (markers.Count == 0) return "keep the claims' [n] markers.";
                    var added = markers.Where(r => !allowed.Contains(r)).ToList();
                    if (added.Count > 0) return $"citations {string.Join(", ", added)} are not among the supported claims; use only their markers.";
                    var newNumbers = ReviewWriter.NumbersIn(a.Text).Where(n => !numbers.Contains(n)).ToList();
                    return newNumbers.Count > 0 ? $"the paragraph had no {string.Join(", ", newNumbers)}; add no number." : null;
                })).Text!.Trim());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (new RepairSenseCheck(field, paragraph, paragraph, KeptBack, $"Does not read well ({problem}); the rewrite failed ({ex.GetType().Name}), so it stays as the repair left it."), null, null);
        }

        // Every citation of the rewrite is checked again, with the same check; the rewrite stays only when all are supported.
        var cited = CitationSupportChecker.ExtractCitedSentences(rewrite, field).ToList();
        List<CitationSupportResult> fresh;
        using (LlmStage.Begin("citation-check"))
            fresh = await CitationSupportChecker.CheckAsync(chat, cited, sources, verifiedFindings: verifiedQuotes, parallelism: options.Parallelism,
                secondCheck: options.SecondCheck, review: options.Review, cache: options.Cache);
        var weak = fresh.Where(r => r.Verdict != CitationSupportChecker.Supported).Select(r => $"[{r.Reference}] {r.Verdict}").ToList();
        if (weak.Count > 0 || fresh.Count < cited.Sum(c => c.References.Count))
            return (new RepairSenseCheck(field, paragraph, paragraph, KeptBack,
                $"Does not read well ({problem}). The rewrite \"{rewrite}\" was checked again and not every citation in it was supported ({(weak.Count > 0 ? string.Join(", ", weak) : "a citation got no verdict")}), so the paragraph stays as the repair left it."), null, null);
        return (new RepairSenseCheck(field, paragraph, rewrite, Rewritten, $"Did not read well ({problem}); rewritten around its supported claims, and every citation in the rewrite was checked again and supported."), rewrite, fresh);
    }

    /// <summary>A validated JSON answer, taken from the review cache when the same prompt was answered before by the same model.</summary>
    private static async Task<T> CachedAsync<T>(IChatCompletionService chat, string prompt, CheckCache? cache, Func<T, string?> validate) where T : class
    {
        string? key = cache == null ? null : ReviewCache.Key(PromptVersion, cache.SenseModel ?? cache.Model, prompt);
        if (key != null && cache!.Cache.TryGet<T>(CacheKind, key, out var hit) && hit != null && validate(hit) == null) return hit;
        var answer = await LlmJson.GetAsync(chat, prompt, LlmJson.JsonMode(0.0), validate);
        if (key != null) cache!.Cache.Set(CacheKind, key, answer);
        return answer;
    }
}

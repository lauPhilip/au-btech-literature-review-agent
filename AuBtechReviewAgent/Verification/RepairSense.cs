using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// One paragraph looked at after the citation repair, as written to citation-audit.json (SenseChecks). Outcome is
/// <see cref="RepairSense.ReadsWell"/>, <see cref="RepairSense.Rewritten"/>, <see cref="RepairSense.KeptBack"/> or
/// <see cref="RepairSense.NotChecked"/>; Before and After are the paragraph before and after this step.
/// </summary>
public sealed record RepairSenseCheck(string Field, string Before, string After, string Outcome, string Note);

/// <summary>
/// Whether a repaired paragraph still reads well (issue #248). A repair checks each sentence for support on its own,
/// so it can leave a paragraph that no longer makes sense, as in a live run: "Three dominant architectures emerge:
/// master orchestrator and hierarchical models", followed by "One model, the master orchestrator, is also described
/// in [7]." Every paragraph with a repaired sentence is read once by the model for sense, with the answer validated in
/// code. A paragraph that does not read well is rewritten once around its supported claims, and every citation in the
/// rewrite is checked again with the same check; the rewrite is kept only when every one of them is supported, so the
/// check is never relaxed. Lists (a line break inside the paragraph) and one-sentence paragraphs, such as the cells
/// of an artifact, are left alone.
/// </summary>
public static partial class RepairSense
{
    public const string ReadsWell = "reads well";
    public const string Rewritten = "rewritten";
    public const string KeptBack = "rewrite kept back";
    public const string NotChecked = "not checked";

    /// <summary>The repair actions that change a sentence.</summary>
    private static readonly HashSet<string> Changes = new(StringComparer.Ordinal) { "rewrite", "drop_citation", "delete" };

    /// <summary>
    /// Reads every repaired paragraph for sense and rewrites the ones that do not read well. <paramref name="before"/>
    /// is the text before the repair, so the paragraph a deleted sentence came from is found. Returns the fields, the
    /// verdicts (rebuilt for a rewritten paragraph) and the log of every paragraph looked at.
    /// </summary>
    public static async Task<(List<(string Field, string Text)> Fields, List<CitationSupportResult> Checks, List<RepairSenseCheck> Log)> CheckAsync(
        IChatCompletionService chat, List<(string Field, string Text)> before, List<(string Field, string Text)> fields,
        List<CitationSupportResult> checks, IReadOnlyList<CitationRepair> repairs, IReadOnlyList<ReferencedPaper> sources,
        IReadOnlyDictionary<int, IReadOnlyList<string>> verifiedQuotes, GroundingOptions options)
    {
        var log = new List<RepairSenseCheck>();
        var texts = fields.ToDictionary(f => f.Field, f => f.Text);
        var earlier = before.ToDictionary(f => f.Field, f => f.Text);
        foreach (var field in repairs.Where(r => Changes.Contains(r.Action)).Select(r => r.Field).Distinct().ToList())
        {
            if (!texts.TryGetValue(field, out var text)) continue;
            var paragraphs = Paragraphs(text);
            var touched = new SortedSet<int>();
            foreach (var repair in repairs.Where(r => r.Field == field && Changes.Contains(r.Action)))
            {
                int at = repair.After.Length > 0 ? paragraphs.FindIndex(p => p.Contains(repair.After, StringComparison.Ordinal)) : -1;
                if (at < 0) at = ParagraphOf(repair.Before, earlier.GetValueOrDefault(field, ""), paragraphs);
                if (at >= 0) touched.Add(at);
            }
            foreach (int index in touched)
            {
                string paragraph = paragraphs[index];
                if (paragraph.Contains('\n') || CitationSupportChecker.SplitSentences(paragraph).Count < 2) continue;
                var changed = repairs.Where(r => r.Field == field && Changes.Contains(r.Action) && (r.After.Length == 0 ? ParagraphOf(r.Before, earlier.GetValueOrDefault(field, ""), paragraphs) == index : paragraph.Contains(r.After, StringComparison.Ordinal))).ToList();
                var (entry, rewrite, fresh) = await ParagraphAsync(chat, field, paragraph, changed, checks, sources, verifiedQuotes, options);
                log.Add(entry);
                if (rewrite == null) continue;
                paragraphs[index] = rewrite;
                texts[field] = string.Join("\n\n", paragraphs);
                checks = Rebuild(checks, field, texts[field], fresh!);
            }
        }
        return (fields.Select(f => (f.Field, texts[f.Field])).ToList(), checks, log);
    }

    /// <summary>The paragraphs of a text, split at blank lines.</summary>
    public static List<string> Paragraphs(string text) =>
        Regex.Split(text.Trim(), @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>
    /// The paragraph that now holds what was around a removed sentence: the one that shares the most sentences with
    /// the paragraph the sentence was in before the repair, or -1.
    /// </summary>
    private static int ParagraphOf(string sentence, string earlierText, List<string> paragraphs)
    {
        var old = Paragraphs(earlierText).FirstOrDefault(p => p.Contains(sentence, StringComparison.Ordinal));
        if (old == null) return -1;
        var oldSentences = CitationSupportChecker.SplitSentences(old).ToHashSet();
        var best = paragraphs.Select((p, i) => (Shared: CitationSupportChecker.SplitSentences(p).Count(oldSentences.Contains), i))
            .OrderByDescending(x => x.Shared).FirstOrDefault();
        return best.Shared > 0 ? best.i : -1;
    }

    /// <summary>
    /// The verdicts of a field after one of its paragraphs was rewritten: a sentence that did not change keeps its
    /// verdicts under its new position, and the rewritten sentences take the fresh ones.
    /// </summary>
    private static List<CitationSupportResult> Rebuild(List<CitationSupportResult> checks, string field, string text, List<CitationSupportResult> fresh)
    {
        var known = new Dictionary<(string, string, int), CitationSupportResult>();
        foreach (var r in checks.Where(c => c.Field == field)) known.TryAdd((r.Field, r.Sentence, r.Reference), r);
        foreach (var r in fresh) known[(r.Field, r.Sentence, r.Reference)] = r;
        var rebuilt = checks.Where(c => c.Field != field).ToList();
        foreach (var s in CitationSupportChecker.ExtractCitedSentences(text, field))
            foreach (var reference in s.References)
                if (known.TryGetValue((field, s.Sentence, reference), out var result))
                {
                    result.SentenceIndex = s.SentenceIndex;
                    rebuilt.Add(result);
                }
        return rebuilt.OrderBy(r => r.Field).ThenBy(r => r.SentenceIndex).ThenBy(r => r.Reference).ToList();
    }
}

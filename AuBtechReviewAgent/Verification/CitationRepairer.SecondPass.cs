using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// A citation the second repair pass removed, as written to citation-audit.json: the sentence as it was, the
/// verdict, quote and reason the citation had, and whether the whole sentence went with it.
/// </summary>
public sealed record RemovedCitation(string Field, string Sentence, int Reference, string Verdict, string? Quote, string Reason, bool SentenceDeleted);

/// <summary>What the second repair pass changed: the texts, the merged verdicts, its log entries and the removed citations.</summary>
public sealed record SecondPassResult(
    List<(string Field, string Text)> Fields,
    List<CitationSupportResult> Results,
    List<CitationRepair> Repairs,
    List<RemovedCitation> Removed);

public static partial class CitationRepairer
{
    /// <summary>
    /// What the second pass sends to the model: a citation that is still partly supported, or a positive verdict
    /// whose quote was not found. A citation that is still not supported is removed in code instead.
    /// </summary>
    public static bool NeedsRewrite(CitationSupportResult r) => NeedsRepair(r) && r.Verdict != CitationSupportChecker.NotSupported;

    /// <summary>
    /// The second repair pass, run after the first when citations are still flagged (issue #135). Partly supported
    /// citations are rewritten once more by <see cref="RepairAsync"/> and checked again. Then every citation that is
    /// still not supported is removed in code, without a model call: the reference goes when another citation backs
    /// the sentence, and the sentence goes when nothing else does. Every change is logged with Pass = 2.
    /// </summary>
    public static async Task<SecondPassResult> SecondPassAsync(
        IChatCompletionService chat, List<(string Field, string Text)> fields, List<CitationSupportResult> results,
        IReadOnlyList<ReferencedPaper> papers, IReadOnlyDictionary<int, IReadOnlyList<string>> findings, int referenceCount,
        int parallelism = 1, bool secondCheck = false, string review = DefaultReview)
    {
        var repairs = new List<CitationRepair>();
        if (results.Any(NeedsRewrite))
        {
            List<CitationRepair> rewritten;
            (fields, results, rewritten) = await RepairAsync(chat, fields, results, papers, findings, referenceCount,
                parallelism, secondCheck, review, NeedsRewrite);
            repairs.AddRange(rewritten.Select(r => r with { Pass = 2 }));
        }
        var removal = RemoveUnsupported(fields, results);
        repairs.AddRange(removal.Repairs);
        return removal with { Repairs = repairs };
    }

    /// <summary>
    /// Removes every citation that is still not supported, in code. When another citation in the sentence is not
    /// itself not supported and the marker can go (<see cref="CanDrop"/>), only the reference is removed; when
    /// nothing else backs the claim, the sentence is deleted; when the marker reads as a noun, the sentence is left
    /// and the log says why. The kept citations of a changed sentence keep their verdicts under its new text, and
    /// every sentence index is recomputed.
    /// </summary>
    public static SecondPassResult RemoveUnsupported(List<(string Field, string Text)> fields, List<CitationSupportResult> results)
    {
        var repairs = new List<CitationRepair>();
        var removed = new List<RemovedCitation>();
        var texts = fields.ToDictionary(f => f.Field, f => f.Text);
        var renamed = new Dictionary<(string Field, string Sentence), string>();

        foreach (var sentence in results.Where(r => r.Verdict == CitationSupportChecker.NotSupported)
                     .GroupBy(r => (r.Field, r.SentenceIndex)).OrderBy(g => g.Key.Field, StringComparer.Ordinal).ThenBy(g => g.Key.SentenceIndex))
        {
            var bad = sentence.GroupBy(r => r.Reference).Select(g => g.First()).OrderBy(r => r.Reference).ToList();
            string field = sentence.Key.Field, text = bad[0].Sentence;
            var badRefs = bad.Select(r => r.Reference).ToList();
            string verdicts = string.Join(", ", bad.Select(r => $"[{r.Reference}] {r.Verdict}"));
            bool others = CitationSupportChecker.ReferencesIn(text).Any(r => !badRefs.Contains(r));

            if (others && !CanDrop(text, badRefs))
            {
                repairs.Add(new CitationRepair(field, text, text, "not applied", badRefs, verdicts,
                    "Still not supported after the repair, but the citation is part of the sentence (the sentence names the source), so removing it would break the sentence; the sentence is unchanged and keeps its verdict.", 2));
                continue;
            }
            string replacement = others ? badRefs.Aggregate(text, RemoveReference) : "";
            if (!Replace(texts, field, text, replacement))
            {
                repairs.Add(new CitationRepair(field, text, text, "not applied", badRefs, verdicts, "The sentence was not found in the section text.", 2));
                continue;
            }
            if (others) renamed[(field, replacement)] = text;
            repairs.Add(new CitationRepair(field, text, replacement, others ? "drop_citation" : "delete", badRefs, verdicts, others
                ? "Still not supported after the repair; the citation was removed and the sentence kept, because its other citations back it."
                : "Still not supported after the repair, and no other citation backs the claim, so the sentence was removed.", 2));
            removed.AddRange(bad.Select(r => new RemovedCitation(field, text, r.Reference, r.Verdict, r.Quote, r.Reason, SentenceDeleted: !others)));
        }

        var newFields = fields.Select(f => (f.Field, texts[f.Field])).ToList();
        if (removed.Count == 0) return new SecondPassResult(newFields, results, repairs, removed);

        // Rebuilt as RepairAsync merges: a kept citation keeps its verdict, under the new sentence text and position.
        var gone = removed.Select(r => (r.Field, r.Sentence, r.Reference)).ToHashSet();
        var previous = results.Where(r => !gone.Contains((r.Field, r.Sentence, r.Reference)))
            .GroupBy(r => (r.Field, r.Sentence, r.Reference)).ToDictionary(g => g.Key, g => g.First());
        var merged = new List<CitationSupportResult>();
        foreach (var s in newFields.SelectMany(f => CitationSupportChecker.ExtractCitedSentences(f.Item2, f.Field)))
        {
            string before = renamed.TryGetValue((s.Field, s.Sentence), out var old) ? old : s.Sentence;
            foreach (int reference in s.References)
                if (previous.TryGetValue((s.Field, before, reference), out var kept))
                {
                    kept.Sentence = s.Sentence;
                    kept.SentenceIndex = s.SentenceIndex;
                    merged.Add(kept);
                }
        }
        var ordered = merged.OrderBy(r => r.Field).ThenBy(r => r.SentenceIndex).ThenBy(r => r.Reference).ToList();
        return new SecondPassResult(newFields, ordered, repairs, removed);
    }

    /// <summary>Puts the replacement in place of the first occurrence of the sentence; an empty one deletes it.</summary>
    private static bool Replace(Dictionary<string, string> texts, string field, string sentence, string replacement)
    {
        if (!texts.TryGetValue(field, out var current)) return false;
        int at = current.IndexOf(sentence, StringComparison.Ordinal);
        if (at < 0) return false;
        string updated = current[..at] + replacement + current[(at + sentence.Length)..];
        if (replacement.Length == 0) updated = Regex.Replace(Regex.Replace(updated, @"[ \t]{2,}", " "), @"[ \t]+\n", "\n");
        texts[field] = updated.Trim();
        return true;
    }
}

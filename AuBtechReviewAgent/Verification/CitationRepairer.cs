using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>One sentence changed (or left alone) by the citation repair pass, as written to citation-audit.json.</summary>
public record CitationRepair(string Field, string Before, string After, string Action, IReadOnlyList<int> FlaggedReferences, string InitialVerdicts, string Note);

/// <summary>
/// The citation repair pass, shared by every kind of review. Sentences whose citation the support check rejected
/// are rewritten once from the cited source's evidence, lose that citation, or are deleted; a rewrite may never add
/// a citation. Only the changed sentences are checked again. Every decision goes into the repair log
/// (citation-audit.json), so nothing is changed silently.
/// </summary>
public static class CitationRepairer
{
    /// <summary>How the prompts name the review when no other kind is given.</summary>
    public const string DefaultReview = "systematic literature review";

    private sealed class RepairAnswer { public List<RepairItem>? Repairs { get; set; } }
    /// <summary>The repair answers for one section, or why the repair call failed.</summary>
    private sealed record RepairBatch(string Field, List<(int Id, string Sentence, List<CitationSupportResult> Checks)> Items, List<RepairItem> Answers, string? Error);
    private sealed class RepairItem { public int Id { get; set; } public string Action { get; set; } = ""; public string? Sentence { get; set; } public string? Reason { get; set; } }

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

    /// <summary>
    /// Should the repair pass look at this verdict? Not supported, partly supported (the sentence claims more
    /// than the paper says), or a positive verdict whose quote was not found. Not "insufficient evidence":
    /// with only an abstract there is nothing to rewrite the sentence from.
    /// </summary>
    public static bool NeedsRepair(CitationSupportResult r) =>
        r.Verdict is CitationSupportChecker.NotSupported or CitationSupportChecker.Partial ||
        (r.Verdict == CitationSupportChecker.Unverifiable && r.EvidenceBasis != "none" && r.Reason.StartsWith("Model said", StringComparison.Ordinal));

    /// <summary>
    /// For every sentence with a rejected citation: one call per section asks the model to rewrite the sentence
    /// from the cited paper's evidence, to drop that citation, or to delete the sentence. A rewrite may only keep
    /// or remove the sentence's own citations (never add new ones). The changed sentences are checked again;
    /// unchanged sentences keep their verdicts. Returns the new texts, the merged results and the repair log.
    /// </summary>
    public static async Task<(List<(string Field, string Text)> Fields, List<CitationSupportResult> Results, List<CitationRepair> Repairs)> RepairAsync(
        IChatCompletionService chat, List<(string Field, string Text)> fields, List<CitationSupportResult> results,
        IReadOnlyList<ReferencedPaper> papers, IReadOnlyDictionary<int, IReadOnlyList<string>> findings, int referenceCount,
        int parallelism = 1, bool secondCheck = false, string review = DefaultReview)
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

        using var throttle = new SemaphoreSlim(Math.Max(1, parallelism));
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
                        data.AppendLine($"  Part attributed to [{check.Reference}]: \"{check.AttributedText}\"");
                        data.AppendLine($"  Evidence from [{check.Reference}]:\n{Evidence(check.Reference, item.Sentence)}");
                    }
                    data.AppendLine();
                }
                string prompt = $$"""
                    You are correcting citations in a {{review}}. An automated check found that the sentences below attribute more to a cited paper than its text supports (not supported, or only partly supported). For each item, choose one action:
                    - "rewrite": return the sentence with the part attributed to each flagged paper changed so that it says what the evidence below says, usually by narrowing it (drop the unsupported number, qualifier, scope or comparison). Change only that part; keep the rest of the sentence, its role in the paragraph and its citation markers (you may remove one, never add a new number).
                    - "drop_citation": the sentence is fine without the flagged paper; the flagged citation will be removed.
                    - "delete": the sentence cannot be supported and should be removed.

                    {{PromptSafety.DataOnlyNotice}}

                    {{PromptSafety.Wrap(data.ToString(), "the flagged sentences and the cited papers' evidence")}}

                    Respond ONLY with a minified JSON object:
                    {"repairs":[{"id":1,"action":"rewrite","sentence":"the corrected sentence with its [n] markers","reason":"one short sentence"}]}
                    """;
                try
                {
                    var answer = await LlmJson.GetAsync<RepairAnswer>(chat, prompt, LlmJson.JsonMode(0.0), a =>
                        a.Repairs == null ? "repairs must be a list." :
                        a.Repairs.Any(r => !LlmJson.OneOf(r.Action, "rewrite", "drop_citation", "delete")) ? "each action must be rewrite, drop_citation or delete." : null);
                    return new RepairBatch(group.Key, items, answer.Repairs!, null);
                }
                catch (Exception ex)
                {
                    return new RepairBatch(group.Key, items, new List<RepairItem>(), $"Repair call failed ({ex.GetType().Name}).");
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
                merged.AddRange(await CitationSupportChecker.CheckAsync(chat, toCheck, papers, verifiedFindings: findings, parallelism: parallelism, secondCheck: secondCheck));
        }
        var ordered = merged.OrderBy(r => r.Field).ThenBy(r => r.SentenceIndex).ThenBy(r => r.Reference).ToList();
        return (newFields, ordered, repairs);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

public static partial class CitationSupportChecker
{
    public static async Task<List<CitationSupportResult>> CheckAsync(
        IChatCompletionService chat,
        IReadOnlyList<CitedSentence> citedSentences,
        IReadOnlyList<ReferencedPaper> papers,
        int excerptsPerReference = 6,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? verifiedFindings = null,
        int parallelism = 1,
        bool secondCheck = false,
        Action<bool, int, int>? progress = null,
        string review = CitationRepairer.DefaultReview,
        CheckCache? cache = null)
    {
        var byReference = papers.ToDictionary(p => p.ReferenceNumber);
        var pairs = citedSentences.SelectMany(s => s.References.Select(r => (Ref: r, Sentence: s))).ToList();
        var results = await RunPassAsync(chat, pairs, byReference, excerptsPerReference, verifiedFindings, parallelism, second: false, progress, review, cache);
        foreach (var r in results) r.FirstVerdict = r.Verdict;

        // Second, independent check for what the first did not find fully supported. It does not see the first
        // verdict and gets twice as many excerpts, chosen for the attributed clause. The final verdict is the
        // better of the two only when that better verdict rests on a verbatim quote found in the paper.
        if (secondCheck)
        {
            var doubtful = results.Where(r => r.Verdict is Partial or NotSupported ||
                                              (r.Verdict == Unverifiable && r.Reason.StartsWith("Model said", StringComparison.Ordinal))).ToList();
            if (doubtful.Count > 0)
            {
                var sentenceOf = citedSentences.ToDictionary(s => (s.Field, s.SentenceIndex));
                var secondPairs = doubtful.Select(r => (Ref: r.Reference, Sentence: sentenceOf[(r.Field, r.SentenceIndex)])).ToList();
                var second = await RunPassAsync(chat, secondPairs, byReference, excerptsPerReference * 2, verifiedFindings, parallelism, second: true, progress, review, cache);
                var secondOf = second.ToDictionary(r => (r.Field, r.SentenceIndex, r.Reference));
                foreach (var r in doubtful)
                {
                    if (!secondOf.TryGetValue((r.Field, r.SentenceIndex, r.Reference), out var s2)) continue;
                    r.SecondVerdict = s2.Verdict;
                    r.SecondQuote = s2.Quote;
                    r.SecondEvidenceLocation = s2.EvidenceLocation;
                    r.SecondReason = s2.Reason;
                    if (Rank(s2.Verdict) > Rank(r.Verdict) && s2.QuoteVerified)
                    {
                        r.Verdict = s2.Verdict;
                        r.Quote = s2.Quote;
                        r.QuoteVerified = true;
                        r.EvidenceLocation = s2.EvidenceLocation;
                        r.Reason = s2.Reason;
                        r.ResolvedBy = "second check";
                    }
                }
            }
        }
        return results.OrderBy(r => r.Field).ThenBy(r => r.SentenceIndex).ThenBy(r => r.Reference).ToList();
    }

    private static async Task<List<CitationSupportResult>> RunPassAsync(
        IChatCompletionService chat, List<(int Ref, CitedSentence Sentence)> pairs,
        IReadOnlyDictionary<int, ReferencedPaper> byReference, int excerptsPerReference,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? verifiedFindings, int parallelism, bool second,
        Action<bool, int, int>? progress = null, string review = CitationRepairer.DefaultReview, CheckCache? cache = null)
    {
        // References are checked a few at a time; the results are sorted afterwards, so the order of the
        // answers does not matter.
        using var throttle = new SemaphoreSlim(Math.Max(1, parallelism));
        var groups = pairs.GroupBy(x => x.Ref).OrderBy(g => g.Key).ToList();
        int finished = 0;
        var work = groups.Select(async group =>
        {
            await throttle.WaitAsync();
            try
            {
                var checkedRef = await CheckReferenceAsync(chat, group.Key, group.Select(x => x.Sentence).ToList(), byReference, excerptsPerReference, verifiedFindings, second, review, cache);
                progress?.Invoke(second, Interlocked.Increment(ref finished), groups.Count);
                return checkedRef;
            }
            finally { throttle.Release(); }
        }).ToList();
        var results = new List<CitationSupportResult>();
        foreach (var task in work) results.AddRange(await task);
        return results;
    }

    private static async Task<List<CitationSupportResult>> CheckReferenceAsync(
        IChatCompletionService chat, int reference, List<CitedSentence> sentences,
        IReadOnlyDictionary<int, ReferencedPaper> byReference, int excerptsPerReference,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? verifiedFindings, bool second = false, string review = CitationRepairer.DefaultReview,
        CheckCache? cache = null)
    {
        var results = new List<CitationSupportResult>();
        var group = (Key: reference, Count: sentences.Count);
        {
            if (!byReference.TryGetValue(group.Key, out var paper))
            {
                results.AddRange(sentences.Select(s => Result(s, group.Key, Unverifiable, "none", "The reference number is not in the reference list.")));
                return results;
            }

            // Evidence: the abstract plus the full-text excerpts most relevant to these sentences.
            var excerpts = new List<(string Label, string Text)>();
            if (!string.IsNullOrWhiteSpace(paper.Abstract)) excerpts.Add(("E1 (abstract)", paper.Abstract.Trim()));
            // The extraction quotes already found in this paper's text: the passages the synthesis was written from.
            if (verifiedFindings != null && verifiedFindings.TryGetValue(group.Key, out var findingQuotes))
                foreach (var q in findingQuotes.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct())
                    excerpts.Add(($"E{excerpts.Count + 1} (verified extraction quote)", q.Trim()));
            // Excerpts are chosen for the clauses attributed to this paper, not for the whole sentences.
            var terms = TextRelevance.Terms(string.Join(" ", sentences.Select(s => AttributedClause(s.Sentence, group.Key))));
            foreach (var chunk in GroundingContextBuilder.SelectChunks(paper.Chunks, terms, excerptsPerReference))
                excerpts.Add(($"E{excerpts.Count + 1} (page {chunk.PageNumber})", chunk.Text));

            string basis = paper.HasFullText ? "full text" : "abstract only";
            if (excerpts.Count == 0)
            {
                results.AddRange(sentences.Select(s => Result(s, group.Key, Unverifiable, "none", "No abstract or full text was available for this paper.")));
                return results;
            }

            var prompt = new StringBuilder();
            prompt.AppendLine(second
                ? $"You are checking citations in a {review} as an independent second reviewer. For each numbered sentence below, decide whether the EXCERPTS from the cited paper support the part of the sentence attributed to this paper. Read all excerpts before deciding: the support may be in any of them."
                : $"You are checking citations in a {review}. For each numbered sentence below, decide whether the EXCERPTS from the cited paper support the part of the sentence attributed to this paper.");
            prompt.AppendLine("The full sentence is given for context only. Other papers cited in it are checked separately, and the review's own interpretation (text not attributed to any paper) is not checked against this paper.");
            prompt.AppendLine();
            prompt.AppendLine($"CITED PAPER [{group.Key}]: {paper.Title}");
            prompt.AppendLine(PromptSafety.DataOnlyNotice);
            prompt.AppendLine("EXCERPTS:");
            prompt.AppendLine(PromptSafety.Wrap(string.Join("\n", excerpts.Select(e => $"{e.Label.Split(' ')[0]}: {e.Text}")), "excerpts from the cited paper"));
            prompt.AppendLine();
            prompt.AppendLine("SENTENCES CITING THIS PAPER:");
            for (int i = 0; i < sentences.Count; i++)
            {
                prompt.AppendLine($"S{i + 1}: {sentences[i].Sentence}");
                prompt.AppendLine($"   Attributed to [{group.Key}]: \"{AttributedClause(sentences[i].Sentence, group.Key)}\"");
            }
            prompt.AppendLine();
            prompt.AppendLine("""
                Rules:
                - "supported": the excerpts state the attributed claim in substance. Different wording, a summary, or a fair generalisation of what the paper reports counts as supported; do not downgrade only because the wording differs.
                - "partially_supported": the core of the attributed claim is in the excerpts, but a specific element it adds is not: a number, a qualifier (such as "always" or "most"), a scope, a causal link or a comparison.
                - "not_supported": the excerpts address the topic but say something different, or contradict the claim.
                - "not_in_excerpts": the excerpts do not mention the topic of the attributed claim at all.
                - When several papers are cited together for one claim or a list, this paper supports it if it supports the claim in substance or the element of the list it evidently contributes.
                - Judge only against the excerpts, not your own knowledge.
                - For supported and partially_supported, copy a short verbatim quote (one sentence, max 40 words) from ONE excerpt and name that excerpt (e.g. "E2"). Copy it as one unbroken passage: do not shorten it with "..." and do not rephrase it.
                Respond ONLY with a minified JSON object:
                {"results":[{"sentence":1,"verdict":"supported","excerpt":"E2","quote":"...","reason":"one short sentence"}]}
                """);

            try
            {
                var settings = new MistralAIPromptExecutionSettings { Temperature = 0.0 };
                settings.ExtensionData ??= new Dictionary<string, object>();
                settings.ExtensionData["response_format"] = new { type = "json_object" };

                // The same prompt (the same sentences, excerpts and quotes) gets the answer it got before, when the
                // caller passes a cache: the key is the whole prompt, so any change to the text or the evidence asks again.
                string promptText = prompt.ToString();
                string? key = cache == null ? null : ReviewCache.Key(CheckPromptVersion, cache.Model, promptText);
                string raw;
                if (key != null && cache!.Cache.TryGet<CachedAnswer>("citation-check", key, out var hit) && hit?.Text is { Length: > 0 } cachedText)
                    raw = cachedText;
                else
                {
                    raw = (await chat.GetChatMessageContentAsync(promptText, settings)).ToString();
                    if (key != null && ParseVerdicts(raw).Count > 0) cache!.Cache.Set("citation-check", key, new CachedAnswer { Text = raw });
                }
                var parsed = ParseVerdicts(raw);

                for (int i = 0; i < sentences.Count; i++)
                {
                    var r = Result(sentences[i], group.Key, Unverifiable, basis, "The model gave no verdict for this sentence.");
                    if (parsed.TryGetValue(i + 1, out var v))
                    {
                        r.Verdict = v.Verdict is Supported or Partial or NotSupported ? v.Verdict : Unverifiable;
                        r.Reason = v.Reason;
                        // Absence from an abstract is not evidence against a claim; absence from the most
                        // relevant full-text passages is treated as not supported.
                        if (v.Verdict == "not_in_excerpts")
                        {
                            r.Verdict = paper.HasFullText ? NotSupported : InsufficientEvidence;
                            r.Reason = $"The excerpts do not mention this. {v.Reason}".Trim();
                        }
                        r.Quote = v.Quote;
                        var excerpt = excerpts.FirstOrDefault(e => e.Label.Split(' ')[0].Equals(v.Excerpt, StringComparison.OrdinalIgnoreCase));
                        r.EvidenceLocation = excerpt.Label;
                        r.QuoteVerified = QuoteOccursIn(v.Quote, excerpt.Text);

                        // A positive verdict must be backed by a quote that is really there.
                        if (r.Verdict is Supported or Partial && !r.QuoteVerified)
                        {
                            r.Verdict = Unverifiable;
                            r.Reason = $"Model said '{v.Verdict}' but its quote could not be found in {v.Excerpt ?? "the excerpts"}. {v.Reason}".Trim();
                        }
                    }
                    results.Add(r);
                }
            }
            catch (Exception ex)
            {
                results.AddRange(sentences.Select(s => Result(s, group.Key, Unverifiable, basis, $"Check failed: {ex.GetType().Name}.")));
            }
        }
        return results;
    }

    public record ParsedVerdict(string Verdict, string? Excerpt, string? Quote, string Reason);

    public static Dictionary<int, ParsedVerdict> ParseVerdicts(string raw)
    {
        var map = new Dictionary<int, ParsedVerdict>();
        if (string.IsNullOrEmpty(raw) || !raw.Contains('{')) return map;

        // Same reading as LlmJson: the first complete object, with line breaks inside quotes made valid.
        using var doc = JsonDocument.Parse(LlmJson.ExtractObject(raw));
        if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
        foreach (var item in arr.EnumerateArray())
        {
            int idx = item.TryGetProperty("sentence", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32()
                    : item.TryGetProperty("sentence", out s) && s.ValueKind == JsonValueKind.String && int.TryParse(s.GetString()?.TrimStart('S', 's'), out int si) ? si : -1;
            if (idx < 1) continue;
            string Str(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            map[idx] = new ParsedVerdict(Str("verdict").Trim().ToLowerInvariant().Replace(' ', '_'), Str("excerpt").Trim(), Str("quote").Trim(), Str("reason").Trim());
        }
        return map;
    }

    private static CitationSupportResult Result(CitedSentence s, int reference, string verdict, string basis, string reason) => new()
    {
        Field = s.Field,
        SentenceIndex = s.SentenceIndex,
        Sentence = s.Sentence,
        Reference = reference,
        Verdict = verdict,
        EvidenceBasis = basis,
        Reason = reason,
        AttributedText = AttributedClause(s.Sentence, reference),
    };
}

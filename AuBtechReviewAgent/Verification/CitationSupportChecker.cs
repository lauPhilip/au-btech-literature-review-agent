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

/// <summary>A sentence in the synthesis or discussion together with the reference numbers it cites.</summary>
public record CitedSentence(string Field, int SentenceIndex, string Sentence, IReadOnlyList<int> References);

/// <summary>The verdict on one (sentence, reference) pair, as written to citation-audit.json.</summary>
public class CitationSupportResult
{
    public string Field { get; set; } = "";
    public int SentenceIndex { get; set; }
    public string Sentence { get; set; } = "";
    public int Reference { get; set; }

    /// <summary>supported, partially_supported, not_supported, or unverifiable (no text available / check failed).</summary>
    public string Verdict { get; set; } = "";

    /// <summary>"full text" or "abstract only": what the check could look at.</summary>
    public string EvidenceBasis { get; set; } = "";
    public string? EvidenceLocation { get; set; }
    public string? Quote { get; set; }

    /// <summary>
    /// True when the quoted passage really occurs in the excerpt the model pointed to. A "supported" verdict
    /// whose quote cannot be found is downgraded, because the model may have invented the evidence.
    /// </summary>
    public bool QuoteVerified { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Set when a theme subsection cites a study that was not coded under that theme.</summary>
    public string? ThemeNote { get; set; }

    /// <summary>The part of the sentence attributed to this reference: what the check judged.</summary>
    public string AttributedText { get; set; } = "";

    /// <summary>The first check's verdict, before the second check (equal to Verdict when no second check ran).</summary>
    public string FirstVerdict { get; set; } = "";

    /// <summary>The independent second check, run on partly supported and not supported citations.</summary>
    public string? SecondVerdict { get; set; }
    public string? SecondQuote { get; set; }
    public string? SecondEvidenceLocation { get; set; }
    public string? SecondReason { get; set; }

    /// <summary>"first check" or "second check": whose verdict is the final one.</summary>
    public string ResolvedBy { get; set; } = "first check";
}

public class CitationSupportSummary
{
    public int Checked { get; set; }
    public int Supported { get; set; }
    public int PartiallySupported { get; set; }
    public int NotSupported { get; set; }
    public int Unverifiable { get; set; }
    public int InsufficientEvidence { get; set; }
    public int SecondChecked { get; set; }
    public int SecondCheckAgreed { get; set; }
    public int UpgradedBySecondCheck { get; set; }

    public static CitationSupportSummary From(IEnumerable<CitationSupportResult> results)
    {
        var list = results.ToList();
        return new CitationSupportSummary
        {
            Checked = list.Count,
            Supported = list.Count(r => r.Verdict == CitationSupportChecker.Supported),
            PartiallySupported = list.Count(r => r.Verdict == CitationSupportChecker.Partial),
            NotSupported = list.Count(r => r.Verdict == CitationSupportChecker.NotSupported),
            Unverifiable = list.Count(r => r.Verdict == CitationSupportChecker.Unverifiable),
            InsufficientEvidence = list.Count(r => r.Verdict == CitationSupportChecker.InsufficientEvidence),
            SecondChecked = list.Count(r => r.SecondVerdict != null),
            SecondCheckAgreed = list.Count(r => r.SecondVerdict != null && r.SecondVerdict == r.FirstVerdict),
            UpgradedBySecondCheck = list.Count(r => r.ResolvedBy == "second check"),
        };
    }

    /// <summary>One sentence for the report, e.g. "Of 14 citations checked, 11 were supported, ...".</summary>
    public string ToSentence()
    {
        if (Checked == 0) return "No inline citations were available for the automated support check.";
        string text = $"An automated check compared the part of each cited sentence attributed to a paper with the text of that paper. Of {Checked} citation{(Checked == 1 ? "" : "s")} checked, " +
               $"{Supported} {(Supported == 1 ? "was" : "were")} judged supported, {PartiallySupported} partially supported, {NotSupported} not supported, " +
               (InsufficientEvidence > 0 ? $"{InsufficientEvidence} could not be judged because only the abstract was available and it does not mention the claim, " : "") +
               $"and {Unverifiable} could not be verified.";
        if (SecondChecked > 0)
            text += $" {SecondChecked} citation{(SecondChecked == 1 ? " that was" : "s that were")} not fully supported went through a second, independent check with more of the paper's text; it agreed with the first check on {SecondCheckAgreed} and found verbatim support that raised the verdict for {UpgradedBySecondCheck}.";
        return text + " Every verdict, with the quoted evidence, is listed in citation-audit.json; these are model judgements and should be spot-checked by a human reviewer.";
    }
}

/// <summary>
/// Closes the gap the range validator leaves open: a citation can point at a real reference that does not
/// actually say what the sentence claims. For every cited reference, the sentences citing it are sent to the
/// model together with the most relevant excerpts of that paper, and the model must answer per sentence
/// with a verdict and a verbatim quote. The quote is then checked against the excerpt in code.
/// Nothing is removed from the report; the results are an audit for the human reviewer.
/// </summary>
public static class CitationSupportChecker
{
    public const string Supported = "supported";
    public const string Partial = "partially_supported";
    public const string NotSupported = "not_supported";
    public const string Unverifiable = "unverifiable";

    /// <summary>Part of the cache key of a check: raise it whenever the check prompt changes.</summary>
    public const string CheckPromptVersion = "citation-check-v1";

    /// <summary>A model answer kept in the cache, as received.</summary>
    public sealed class CachedAnswer { public string Text { get; set; } = ""; }
    public const string InsufficientEvidence = "insufficient_evidence";

    /// <summary>Rank of a verdict for the second-check rule: only verified support counts.</summary>
    public static int Rank(string verdict) => verdict switch { Supported => 2, Partial => 1, _ => 0 };

    /// <summary>
    /// The part of a sentence attributed to one reference: the text between the previous citation marker (or
    /// the start of the sentence) and the marker that cites it. Text after the last marker is the review's own
    /// interpretation and is not attributed to any paper. Falls back to the whole sentence when the clause is
    /// too short to stand alone (e.g. "Studies [3] show ...").
    /// </summary>
    public static string AttributedClause(string sentence, int reference)
    {
        var markers = MarkerPattern.Matches(sentence).Cast<Match>().ToList();
        string Plain(string t) => Regex.Replace(MarkerPattern.Replace(t, ""), @"\s{2,}", " ").Trim();
        int index = markers.FindIndex(m => ReferencesIn(m.Value).Contains(reference));
        if (index < 0) return Plain(sentence);
        int start = index == 0 ? 0 : markers[index - 1].Index + markers[index - 1].Length;
        string clause = sentence[start..markers[index].Index];
        clause = Regex.Replace(clause, @"^[\s,;:.\u2014\u2013-]*(and|or|while|whereas|but|yet|with|from|to|as well as)?\s+", "", RegexOptions.IgnoreCase);
        clause = Plain(clause).Trim(' ', ',', ';', ':', '\u2014', '\u2013', '-');
        if (clause.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
            clause = Plain(sentence[..markers[index].Index]).Trim(' ', ',', ';', ':');
        if (clause.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4) clause = Plain(sentence);
        return clause;
    }

    /// <summary>Text after the last citation marker (three words or more): the review's own interpretation.</summary>
    public static string? UnattributedTail(string sentence)
    {
        var markers = MarkerPattern.Matches(sentence).Cast<Match>().ToList();
        if (markers.Count == 0) return null;
        var last = markers[^1];
        string tail = sentence[(last.Index + last.Length)..].Trim().Trim(',', ';', ':', '.', ' ', '\u2014', '\u2013', '-');
        return tail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 ? tail : null;
    }

    private static readonly Regex MarkerPattern = new(@"\[(\d+(?:\s*[,–-]\s*\d+)*)\]", RegexOptions.Compiled);

    /// <summary>Splits text into sentences and returns the ones carrying [n] markers (ranges like [2-4] are expanded).</summary>
    public static IReadOnlyList<CitedSentence> ExtractCitedSentences(string text, string field)
    {
        var result = new List<CitedSentence>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var sentences = SplitSentences(text);
        for (int i = 0; i < sentences.Count; i++)
        {
            var refs = ReferencesIn(sentences[i]);
            if (refs.Count > 0) result.Add(new CitedSentence(field, i, sentences[i], refs));
        }
        return result;
    }

    /// <summary>
    /// The sentence split used by the check. The Review Output page uses the same split, so a clicked [n] in
    /// sentence i finds the verdict recorded for sentence i.
    /// </summary>
    public static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        // Split after ., ! or ? (optionally following a citation marker) when the next sentence starts.
        return Regex.Split(text.Trim(), @"(?<=[.!?])\s+(?=[A-Z\[(""])").Select(s => s.Trim()).ToList();
    }

    /// <summary>Reference numbers cited in one marker text or sentence; ranges like [2-4] are expanded.</summary>
    public static IReadOnlyList<int> ReferencesIn(string sentence)
    {
        var refs = new SortedSet<int>();
        foreach (Match m in MarkerPattern.Matches(sentence))
        {
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var range = part.Split(new[] { '-', '–' }, StringSplitOptions.TrimEntries);
                if (range.Length == 2 && int.TryParse(range[0], out int a) && int.TryParse(range[1], out int b) && b >= a && b - a < 50)
                    for (int n = a; n <= b; n++) refs.Add(n);
                else if (int.TryParse(part.Trim(), out int single))
                    refs.Add(single);
            }
        }
        return refs.ToList();
    }

    /// <summary>Splits a sentence into plain text and citation markers, in order (for rendering clickable markers).</summary>
    public static IReadOnlyList<(string Text, bool IsMarker)> SplitMarkers(string sentence)
    {
        var parts = new List<(string, bool)>();
        int pos = 0;
        foreach (Match m in MarkerPattern.Matches(sentence))
        {
            if (m.Index > pos) parts.Add((sentence[pos..m.Index], false));
            parts.Add((m.Value, true));
            pos = m.Index + m.Length;
        }
        if (pos < sentence.Length) parts.Add((sentence[pos..], false));
        return parts;
    }

    /// <summary>True when the quote (whitespace/case-insensitive, at least 20 characters) occurs in the excerpt.</summary>
    public static bool QuoteOccursIn(string? quote, string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(excerpt)) return false;
        string text = Canonical(excerpt);

        // Models often shorten a quote with "..." (seen in real runs); every part must then occur, in order.
        // Parts shorter than 10 letters are ignored, so an ellipsis cannot turn a quote into loose keywords.
        // A joining word after an ellipsis ("... and use tools") is the model's glue, not part of the source.
        var parts = Regex.Split(quote, @"\.{3}|…")
            .Select((p, i) => i == 0 ? p : Regex.Replace(p, @"^[\s,;:]*(and|or|but)\s+", "", RegexOptions.IgnoreCase))
            .Select(Canonical).Where(p => p.Length > 0).ToList();
        if (parts.Count > 1) parts = parts.Where(p => p.Length >= 10).ToList();
        if (parts.Count == 0 || parts.Sum(p => p.Length) < 15) return false;

        int from = 0;
        foreach (var part in parts)
        {
            int at = text.IndexOf(part, from, StringComparison.Ordinal);
            if (at < 0) return false;
            from = at + part.Length;
        }
        return true;
    }

    /// <summary>
    /// Letters and digits only, lower case, after Unicode normalisation. PDF text has line-break hyphens,
    /// ligatures ("ﬁ"), odd quotes and spacing that a model does not reproduce; comparing only the letters
    /// and digits makes a faithful quote match while a paraphrase still does not.
    /// </summary>
    public static string Canonical(string s)
    {
        string text = s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

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

/// <summary>A cache for the citation check's answers, and the model they came from (part of the key).</summary>
public sealed record CheckCache(ReviewCache Cache, string Model);

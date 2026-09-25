using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
}

public class CitationSupportSummary
{
    public int Checked { get; set; }
    public int Supported { get; set; }
    public int PartiallySupported { get; set; }
    public int NotSupported { get; set; }
    public int Unverifiable { get; set; }

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
        };
    }

    /// <summary>One sentence for the report, e.g. "Of 14 citations checked, 11 were supported, ...".</summary>
    public string ToSentence()
    {
        if (Checked == 0) return "No inline citations were available for the automated support check.";
        return $"An automated check compared each cited sentence with the text of the cited paper. Of {Checked} citation{(Checked == 1 ? "" : "s")} checked, " +
               $"{Supported} {(Supported == 1 ? "was" : "were")} judged supported, {PartiallySupported} partially supported, {NotSupported} not supported, " +
               $"and {Unverifiable} could not be verified. Every verdict, with the quoted evidence, is listed in citation-audit.json; these are model judgements and should be spot-checked by a human reviewer.";
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

    private static readonly Regex MarkerPattern = new(@"\[(\d+(?:\s*[,–-]\s*\d+)*)\]", RegexOptions.Compiled);

    /// <summary>Splits text into sentences and returns the ones carrying [n] markers (ranges like [2-4] are expanded).</summary>
    public static IReadOnlyList<CitedSentence> ExtractCitedSentences(string text, string field)
    {
        var result = new List<CitedSentence>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        // Split after ., ! or ? (optionally following a citation marker) when the next sentence starts.
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+(?=[A-Z\[(""])");
        for (int i = 0; i < sentences.Length; i++)
        {
            string sentence = sentences[i].Trim();
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
            if (refs.Count > 0) result.Add(new CitedSentence(field, i, sentence, refs.ToList()));
        }
        return result;
    }

    /// <summary>True when the quote (whitespace/case-insensitive, at least 20 characters) occurs in the excerpt.</summary>
    public static bool QuoteOccursIn(string? quote, string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(excerpt)) return false;
        static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('’', '\'').Replace('“', '"').Replace('”', '"'), @"\s+", " ").Trim().Trim('"', '\'', '.', '…');
        string q = Norm(quote);
        return q.Length >= 20 && Norm(excerpt).Contains(q);
    }

    public static async Task<List<CitationSupportResult>> CheckAsync(
        IChatCompletionService chat,
        IReadOnlyList<CitedSentence> citedSentences,
        IReadOnlyList<ReferencedPaper> papers,
        int excerptsPerReference = 6)
    {
        var results = new List<CitationSupportResult>();
        var byReference = papers.ToDictionary(p => p.ReferenceNumber);

        var pairsByRef = citedSentences
            .SelectMany(s => s.References.Select(r => (Ref: r, Sentence: s)))
            .GroupBy(x => x.Ref)
            .OrderBy(g => g.Key);

        foreach (var group in pairsByRef)
        {
            var sentences = group.Select(x => x.Sentence).ToList();
            if (!byReference.TryGetValue(group.Key, out var paper))
            {
                results.AddRange(sentences.Select(s => Result(s, group.Key, Unverifiable, "none", "The reference number is not in the reference list.")));
                continue;
            }

            // Evidence: the abstract plus the full-text excerpts most relevant to these sentences.
            var excerpts = new List<(string Label, string Text)>();
            if (!string.IsNullOrWhiteSpace(paper.Abstract)) excerpts.Add(("E1 (abstract)", paper.Abstract.Trim()));
            var terms = TextRelevance.Terms(string.Join(" ", sentences.Select(s => s.Sentence)));
            foreach (var chunk in GroundingContextBuilder.SelectChunks(paper.Chunks, terms, excerptsPerReference))
                excerpts.Add(($"E{excerpts.Count + 1} (page {chunk.PageNumber})", chunk.Text));

            string basis = paper.HasFullText ? "full text" : "abstract only";
            if (excerpts.Count == 0)
            {
                results.AddRange(sentences.Select(s => Result(s, group.Key, Unverifiable, "none", "No abstract or full text was available for this paper.")));
                continue;
            }

            var prompt = new StringBuilder();
            prompt.AppendLine("You are checking citations in a systematic literature review. For each numbered sentence below, decide whether the EXCERPTS from the cited paper support what the sentence says about that paper.");
            prompt.AppendLine();
            prompt.AppendLine($"CITED PAPER [{group.Key}]: {paper.Title}");
            prompt.AppendLine("EXCERPTS:");
            foreach (var (label, text) in excerpts) prompt.AppendLine($"{label.Split(' ')[0]}: {text}");
            prompt.AppendLine();
            prompt.AppendLine("SENTENCES CITING THIS PAPER:");
            for (int i = 0; i < sentences.Count; i++) prompt.AppendLine($"S{i + 1}: {sentences[i].Sentence}");
            prompt.AppendLine();
            prompt.AppendLine("""
                Rules:
                - "supported": the excerpts clearly state what the sentence attributes to this paper.
                - "partially_supported": the excerpts support part of the claim, or support it only loosely.
                - "not_supported": the excerpts do not say this, or contradict it.
                - Judge only against the excerpts, not your own knowledge. If the sentence cites several papers, judge only the part that concerns this one.
                - For supported and partially_supported, copy a short verbatim quote (one sentence, max 40 words) from ONE excerpt and name that excerpt (e.g. "E2").
                Respond ONLY with a minified JSON object:
                {"results":[{"sentence":1,"verdict":"supported","excerpt":"E2","quote":"...","reason":"one short sentence"}]}
                """);

            try
            {
                var settings = new MistralAIPromptExecutionSettings { Temperature = 0.0 };
                settings.ExtensionData ??= new Dictionary<string, object>();
                settings.ExtensionData["response_format"] = new { type = "json_object" };

                var response = await chat.GetChatMessageContentAsync(prompt.ToString(), settings);
                var parsed = ParseVerdicts(response.ToString());

                for (int i = 0; i < sentences.Count; i++)
                {
                    var r = Result(sentences[i], group.Key, Unverifiable, basis, "The model gave no verdict for this sentence.");
                    if (parsed.TryGetValue(i + 1, out var v))
                    {
                        r.Verdict = v.Verdict is Supported or Partial or NotSupported ? v.Verdict : Unverifiable;
                        r.Reason = v.Reason;
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

        return results.OrderBy(r => r.Field).ThenBy(r => r.SentenceIndex).ThenBy(r => r.Reference).ToList();
    }

    public record ParsedVerdict(string Verdict, string? Excerpt, string? Quote, string Reason);

    public static Dictionary<int, ParsedVerdict> ParseVerdicts(string raw)
    {
        var map = new Dictionary<int, ParsedVerdict>();
        string json = raw.Replace("```json", "").Replace("```", "").Trim();
        int start = json.IndexOf('{'), end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return map;

        using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
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
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The reviewer's edits of a finished report, put into its text in code. The unit of an edit is one sentence, as the
/// citation check splits it (<see cref="CitationSupportChecker.SplitSentences"/>): every verdict in citation-audit.json
/// belongs to one (field, sentence) pair, so an edited sentence is checked again on its own and its verdicts replace
/// exactly the ones it had. An edit must stay one sentence, so the sentence numbers of a field never move and every
/// edit is kept against the text the run wrote. Only the written prose can be edited; the method, the counts, the flow
/// diagram, the references and the statement on AI use are written in code from the run's record and stay as they are.
/// </summary>
public static class ReportEdits
{
    /// <summary>The longest edited sentence, in characters.</summary>
    public const int MaxSentenceLength = 1000;

    private static readonly Regex Separator = new(@"(?<=[.!?])\s+(?=[A-Z\[(""])", RegexOptions.Compiled);
    private static readonly Regex Blocks = new(@"\[(MERMAID|TIKZ)_START\]", RegexOptions.Compiled);

    private static readonly string[] Fixed =
    {
        "rationaleItem", "synthesisResultsItem", "discussionItem", "evidenceLimitationsItem", "implicationsItem",
        "futureResearchItem", "conclusionItem",
    };

    /// <summary>
    /// True for the fields the reviewer may edit: the prose the model wrote and the citation check read. Everything
    /// else (title, abstract, method, counts, references, declarations, the check and coverage sentences) is not.
    /// </summary>
    public static bool IsEditable(string field) =>
        Fixed.Contains(field, StringComparer.Ordinal) ||
        Regex.IsMatch(field ?? "", @"^(synthesis-\d{1,3}|researchAnswer\d{1,2})$");

    /// <summary>The sentences of a text as (start, length) in the text, the same sentences as SplitSentences gives.</summary>
    public static IReadOnlyList<(int Start, int Length)> SentenceSpans(string text)
    {
        var spans = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(text)) return spans;
        int start = text.Length - text.TrimStart().Length, end = text.TrimEnd().Length;
        foreach (Match m in Separator.Matches(text[..end], start))
        {
            spans.Add((start, m.Index - start));
            start = m.Index + m.Length;
        }
        spans.Add((start, end - start));
        return spans;
    }

    /// <summary>The text of one field as the run wrote it; null when the report has no such field.</summary>
    public static string? FieldText(PrismaReport report, string field)
    {
        if (field.StartsWith("synthesis-", StringComparison.Ordinal))
            return report.SynthesisSections?.FirstOrDefault(s => s.Field == field)?.Text;
        if (field.StartsWith("researchAnswer", StringComparison.Ordinal) && int.TryParse(field["researchAnswer".Length..], out int n))
            return report.ResearchAnswers?.FirstOrDefault(a => a.Number == n)?.Answer;
        return field switch
        {
            "rationaleItem" => report.RationaleItem,
            "synthesisResultsItem" => Prose(report.SynthesisResultsItem).Prose,
            "discussionItem" => report.DiscussionItem,
            "evidenceLimitationsItem" => report.EvidenceLimitationsItem,
            "implicationsItem" => report.ImplicationsItem,
            "futureResearchItem" => report.FutureResearchItem,
            "conclusionItem" => report.ConclusionItem,
            _ => null,
        };
    }

    /// <summary>The overview keeps its diagram blocks after the prose; only the prose is edited.</summary>
    private static (string Prose, string Tail) Prose(string? text)
    {
        text ??= "";
        var m = Blocks.Match(text);
        return m.Success ? (text[..m.Index], text[m.Index..]) : (text, "");
    }

    /// <summary>
    /// The edited sentence, cleaned like every other typed text, when it may replace sentence <paramref name="index"/>
    /// of <paramref name="text"/>; otherwise the reason it may not, in plain words.
    /// </summary>
    public static (string? Sentence, string? Error) Prepare(string text, int index, string? typed, int referenceCount)
    {
        var spans = SentenceSpans(text);
        if (index < 0 || index >= spans.Count) return (null, "That sentence is not in this part of the report.");
        string clean = Regex.Replace(ReviewInputGuard.Normalize(typed, singleLine: true), @"\s{2,}", " ").Trim();
        if (clean.Length == 0) return (null, "Write the sentence. An empty sentence cannot be saved.");
        if (clean.Length > MaxSentenceLength) return (null, $"A sentence can be at most {MaxSentenceLength} characters.");
        if (clean == text.Substring(spans[index].Start, spans[index].Length)) return (null, "The sentence has not changed.");
        var outside = CitationSupportChecker.ReferencesIn(clean).Where(r => r < 1 || r > referenceCount).ToList();
        if (outside.Count > 0)
            return (null, $"[{string.Join("], [", outside)}] is not in the reference list. Cite only studies from the list (1 to {referenceCount}).");
        // Still one sentence in place, so no other sentence of the field moves or joins it.
        var after = CitationSupportChecker.SplitSentences(Replace(text, spans, new Dictionary<int, string> { [index] = clean }));
        if (after.Count != spans.Count || after[index] != clean)
            return (null, "Write one sentence that starts with a capital letter and ends with a full stop, question mark or exclamation mark.");
        return (clean, null);
    }

    /// <summary>The text with the given sentences replaced, everything between them kept as it was.</summary>
    public static string Apply(string text, IReadOnlyDictionary<int, string> sentences)
    {
        if (sentences.Count == 0 || string.IsNullOrEmpty(text)) return text;
        return Replace(text, SentenceSpans(text), sentences);
    }

    private static string Replace(string text, IReadOnlyList<(int Start, int Length)> spans, IReadOnlyDictionary<int, string> sentences)
    {
        var result = new System.Text.StringBuilder(text.Length);
        int from = 0;
        foreach (var (index, sentence) in sentences.Where(s => s.Key >= 0 && s.Key < spans.Count).OrderBy(s => s.Key))
        {
            result.Append(text, from, spans[index].Start - from).Append(sentence);
            from = spans[index].Start + spans[index].Length;
        }
        return result.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>
    /// A copy of the report with the reviewer's current sentences in place, and a sentence on the edits after the
    /// citation-check paragraph. The same report object comes back when there are no edits, so a run without edits
    /// is written exactly as before.
    /// </summary>
    public static PrismaReport Apply(PrismaReport report, ReportEditLog? log)
    {
        var current = log?.Current() ?? new Dictionary<(string Field, int SentenceIndex), ReportEdit>();
        if (current.Count == 0) return report;
        var copy = JsonSerializer.Deserialize<PrismaReport>(JsonSerializer.Serialize(report))!;
        foreach (var group in current.Values.GroupBy(e => e.Field))
        {
            var sentences = group.ToDictionary(e => e.SentenceIndex, e => e.After);
            string? text = FieldText(copy, group.Key);
            if (text == null || !IsEditable(group.Key)) continue;
            SetField(copy, group.Key, Apply(text, sentences));
        }
        copy.CitationCheckSummary = (copy.CitationCheckSummary + " " + log!.Sentence()).Trim();
        return copy;
    }

    /// <summary>Puts a field's text back in place; a field FieldText does not know is left as it is.</summary>
    internal static void SetField(PrismaReport report, string field, string text)
    {
        if (field.StartsWith("synthesis-", StringComparison.Ordinal))
        {
            report.SynthesisSections.First(s => s.Field == field).Text = text;
            return;
        }
        if (field.StartsWith("researchAnswer", StringComparison.Ordinal))
        {
            int n = int.Parse(field["researchAnswer".Length..], System.Globalization.CultureInfo.InvariantCulture);
            report.ResearchAnswers.First(a => a.Number == n).Answer = text;
            return;
        }
        switch (field)
        {
            case "rationaleItem": report.RationaleItem = text; break;
            case "synthesisResultsItem": report.SynthesisResultsItem = text + Prose(report.SynthesisResultsItem).Tail; break;
            case "discussionItem": report.DiscussionItem = text; break;
            case "evidenceLimitationsItem": report.EvidenceLimitationsItem = text; break;
            case "implicationsItem": report.ImplicationsItem = text; break;
            case "futureResearchItem": report.FutureResearchItem = text; break;
            case "conclusionItem": report.ConclusionItem = text; break;
        }
    }

    /// <summary>
    /// The verdicts of the text as it reads now: the run's verdicts, with those of every edited sentence replaced by
    /// the verdicts its edit got.
    /// </summary>
    public static List<CitationSupportResult> Checks(IEnumerable<CitationSupportResult> audit, ReportEditLog? log)
    {
        var current = log?.Current() ?? new Dictionary<(string Field, int SentenceIndex), ReportEdit>();
        if (current.Count == 0) return audit.ToList();
        return audit.Where(c => !current.ContainsKey((c.Field, c.SentenceIndex)))
            .Concat(current.Values.SelectMany(e => e.Checks))
            .OrderBy(c => c.Field, StringComparer.Ordinal).ThenBy(c => c.SentenceIndex).ThenBy(c => c.Reference)
            .ToList();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

public static partial class StudyExtractor
{
    /// <summary>The most words in a method or sample value, which is an answer, not a sentence (issue #130).</summary>
    public const int ShortValueWords = 12;
    public const int SentenceWords = 30;

    /// <summary>"1.1" from "1.1", " 1.1 " or "1.1 The qualitative approach ...".</summary>
    private static string CriterionId(string? id) => Regex.Match(id ?? "", @"\d\.\d").Value;

    /// <summary>yes, no or cant_tell; "can't tell", "cannot tell" and "cant tell" mean cant_tell.</summary>
    private static string NormalAnswer(string? answer)
    {
        string a = (answer ?? "").Trim().ToLowerInvariant().Replace('\u2019', '\'');
        return a is "can't tell" or "cannot tell" or "cant tell" or "can't_tell" ? "cant_tell" : a;
    }

    private static string? BasicProblem(ExtractionAnswer a)
    {
        if (string.IsNullOrWhiteSpace(a.StudyType)) return "studyType is missing.";
        if (a.AppraisalCategory != "not_empirical" && !MmatCriteria.ContainsKey(a.AppraisalCategory))
            return $"appraisalCategory must be one of: {string.Join(", ", MmatCriteria.Keys)}, not_empirical.";
        if (a.Appraisal != null && a.Appraisal.Any(x => !LlmJson.OneOf(NormalAnswer(x.Answer), "yes", "no", "cant_tell")))
            return "each appraisal answer must be yes, no or cant_tell.";
        return null;
    }

    /// <summary>
    /// The checks an answer should pass but that are not worth losing the whole extraction for: short values that
    /// are not the quote and keep their % signs (issue #130), and all five MMAT answers, each yes or no backed by a
    /// quote that is in the text, not all "can't tell" when the full text was read (issue #149).
    /// </summary>
    internal static string? StrictProblem(ExtractionAnswer a, string evidenceText, bool fullText)
    {
        var fields = new List<(string Name, EvidencedValue? Value, int MaxWords)>
        {
            ("method", a.Method, ShortValueWords), ("sample", a.Sample, ShortValueWords), ("limitations", a.Limitations, SentenceWords),
        };
        var findings = a.KeyFindings ?? new();
        for (int i = 0; i < findings.Count; i++) fields.Add(($"keyFindings[{i}]", findings[i], SentenceWords));
        foreach (var (name, v, maxWords) in fields)
        {
            string value = (v?.Value ?? "").Trim(), quote = (v?.Quote ?? "").Trim();
            if (value.Length == 0 || value.Equals("not reported", StringComparison.OrdinalIgnoreCase)) continue;
            if (Words(value) > maxWords)
                return $"{name}.value has {Words(value)} words; give a short value of at most {maxWords} words in your own words and keep the sentence in the quote.";
            if (maxWords == ShortValueWords && Words(quote) > 5 && Canonical(value) == Canonical(quote))
                return $"{name}.value repeats the quote; give a short value in your own words.";
            foreach (Match m in Percent.Matches(quote))
                if (Regex.IsMatch(value, $@"(?<![\d.,]){Regex.Escape(m.Groups[1].Value)}(?![\d.,]*\s?%)(?![\d.,])"))
                    return $"{name}.value gives {m.Groups[1].Value} without the % sign that the quote has.";
        }

        if (!MmatCriteria.TryGetValue(a.AppraisalCategory, out var criteria)) return null;
        var given = a.Appraisal ?? new();
        var ids = criteria.Select(c => c.Split(' ')[0]).ToList();
        var missing = ids.Where(id => !given.Any(x => CriterionId(x.Id) == id)).ToList();
        if (missing.Count > 0)
            return $"appraisal must answer all five criteria of {a.AppraisalCategory} by id; missing: {string.Join(", ", missing)}.";
        foreach (var x in given.Where(x => NormalAnswer(x.Answer) != "cant_tell"))
            if (!CitationSupportChecker.QuoteOccursIn(x.Quote, evidenceText))
                return $"the quote for {CriterionId(x.Id)} is not in the text word for word; copy one exact sentence that shows the answer, or answer cant_tell.";
        if (fullText && given.All(x => NormalAnswer(x.Answer) == "cant_tell"))
            return "every criterion is cant_tell, but passages from the full text are given; answer each criterion they show with yes or no and a quote, and keep cant_tell for the rest.";
        return null;
    }

    private static readonly Regex Percent = new(@"(\d+(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant);
    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    private static string Canonical(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static EvidencedValue Verify(EvidencedValue? v, string evidenceText)
    {
        var value = v ?? new EvidencedValue { Value = "not reported" };
        value.Value = (value.Value ?? "").Trim();
        value.Quote = (value.Quote ?? "").Trim();
        value.QuoteVerified = CitationSupportChecker.QuoteOccursIn(value.Quote, evidenceText);
        return value;
    }
}

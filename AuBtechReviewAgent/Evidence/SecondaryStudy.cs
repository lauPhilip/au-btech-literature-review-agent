using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Included studies that are themselves reviews (a systematic review, a meta-analysis, a scoping review, a literature
/// survey) are secondary studies: they summarise other studies and add no new evidence of their own. In a real run a
/// systematic review alone carried the answer to RQ1. The class is decided in code and only from the paper's own words:
/// its title, or the verbatim quote behind its method when that quote was found in the paper's text and the model's
/// study type also names a review design. The model's study type alone never makes a study secondary (AGENTS.md rule 1).
/// Table A.2 labels them, the methods name them and an answer that rests on them is marked.
/// </summary>
public static class SecondaryStudy
{
    // Review designs, named in the study type or the title. Each pattern maps to the label the report prints.
    private static readonly (Regex Pattern, string Kind)[] Kinds =
    {
        (new Regex(@"\bmeta[\s-]?analys[ie]s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "meta-analysis"),
        (new Regex(@"\bumbrella review\b|\breview of (?:systematic )?reviews\b|\btertiary study\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "umbrella review"),
        (new Regex(@"\bscoping review\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "scoping review"),
        (new Regex(@"\bsystematic (?:literature |mapping )?(?:review|mapping|map)\b|\bmapping study\b|\brapid review\b|\bmultivocal (?:literature )?review\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "systematic review"),
        (new Regex(@"\b(?:literature|narrative|state[\s-]of[\s-]the[\s-]art|critical|integrative) review\b|\breview of (?:the )?literature\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "literature review"),
        (new Regex(@"\bliterature survey\b|\bsurvey of (?:the )?literature\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "survey"),
    };

    // "Survey" alone also names a questionnaire study, which is primary. A title such as "A survey of agent memory"
    // is a literature survey; a study type of just "survey" is not counted, since the extraction prompt uses it for questionnaires.
    private static readonly Regex SurveyTitle = new(@"^\s*(?:an?\s+)?(?:\w+\s+){0,2}survey\s+(?:of|on)\b(?!.{0,40}\b(?:practitioners|respondents|participants|users|developers|employees|students|engineers|managers)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The kind of review design one text names ("systematic review", "meta-analysis", ...), or null.</summary>
    public static string? KindIn(string? text)
    {
        foreach (var (pattern, kind) in Kinds)
            if (pattern.IsMatch(text ?? "")) return kind;
        return null;
    }

    /// <summary>The kind of review design a title names, including a literature survey ("A survey of agent memory").</summary>
    public static string? KindInTitle(string? title) => KindIn(title) ?? (SurveyTitle.IsMatch(title ?? "") ? "survey" : null);

    /// <summary>
    /// The kind of secondary study and the verbatim text it rests on, or null for a primary study or a failed extraction.
    /// A verified method quote counts only when the model's study type names a review design too, so a primary study
    /// whose method mentions an earlier review ("following a systematic review, we interviewed ...") is not counted
    /// unless the model also called it a review.
    /// </summary>
    public static (string Kind, string Basis, string Text)? Classify(StudyExtraction e)
    {
        if (e.Error != null) return null;
        if (KindInTitle(e.Title) is { } fromTitle) return (fromTitle, "title", e.Title);
        if (e.Method is { QuoteVerified: true } method && KindIn(method.Quote) is { } fromQuote && KindIn(e.StudyType) != null)
            return (fromQuote, "method quote", method.Quote);
        return null;
    }

    /// <summary>The kind for one extraction, or null when it is primary or nothing was extracted from it.</summary>
    public static string? KindOf(StudyExtraction e) => Classify(e)?.Kind;

    /// <summary>The run's secondary studies by reference number, with their kind.</summary>
    public static SortedDictionary<int, string> In(IEnumerable<StudyExtraction>? extractions)
    {
        var result = new SortedDictionary<int, string>();
        foreach (var e in extractions ?? Enumerable.Empty<StudyExtraction>())
            if (KindOf(e) is { } kind) result.TryAdd(e.ReferenceNumber, kind);
        return result;
    }

    /// <summary>
    /// How many included studies are reviews, for the article info. It counts every secondary study decided by <see cref="Classify"/>
    /// and also a study whose extracted study type names a review design ("systematic literature review"), since the reader
    /// is told only how many of the included studies are reviews. A bare "survey" is not counted (it names questionnaires too).
    /// </summary>
    public static int ReviewCount(IEnumerable<StudyExtraction>? extractions)
    {
        var all = (extractions ?? Enumerable.Empty<StudyExtraction>()).Where(e => e.Error == null).ToList();
        return all.Where(e => KindOf(e) != null || KindIn(e.StudyType) != null).Select(e => e.ReferenceNumber).Distinct().Count();
    }

    /// <summary>The methods sentence on the secondary studies; empty when the run included none.</summary>
    public static string MethodsSentence(IReadOnlyDictionary<int, string> secondary)
    {
        if (secondary.Count == 0) return "";
        string list = string.Join(", ", secondary.Select(s => $"{Article(s.Value)} {s.Value} [{s.Key}]"));
        bool one = secondary.Count == 1;
        return $"{(one ? "One included study is a secondary study, a review of other studies" : $"{Words(secondary.Count)} included studies are secondary studies, reviews of other studies")} ({list}), decided in code from {(one ? "its" : "each")} title or from a quote of {(one ? "its" : "the")} method found word for word in {(one ? "its" : "the study's")} text. " +
               $"{(one ? "It summarises" : "They summarise")} the work of others and add{(one ? "s" : "")} no new evidence, so {(one ? "it is" : "they are")} labelled as secondary in Table A.2, and an answer to a research question that cites {(one ? "it" : "them")} is marked as resting on a secondary study.";
    }

    /// <summary>
    /// The note under an answer that cites a secondary study, or null. An answer that cites only secondary studies rests on
    /// them alone; one that also cites primary studies is marked for the secondary ones it cites.
    /// </summary>
    public static string? AnswerNote(string? answer, IReadOnlyDictionary<int, string>? secondary)
    {
        if (secondary is not { Count: > 0 } || string.IsNullOrWhiteSpace(answer)) return null;
        var cited = CitationSupportChecker.ReferencesIn(answer).Distinct().OrderBy(r => r).ToList();
        var reviews = cited.Where(secondary.ContainsKey).ToList();
        if (reviews.Count == 0) return null;
        string named = string.Join(" and ", reviews.Select(r => $"{Article(secondary[r])} {secondary[r]} [{r}]"));
        return reviews.Count == cited.Count
            ? $"This answer rests only on {named}, a secondary study that summarises other studies; no primary study included in this review supports it."
            : $"This answer also cites {named}, a secondary study that summarises other studies rather than adding new evidence.";
    }

    private static string Article(string kind) => "aeiou".Contains(char.ToLowerInvariant(kind[0])) ? "an" : "a";

    private static string Words(int n) => n switch
    {
        2 => "Two", 3 => "Three", 4 => "Four", 5 => "Five", 6 => "Six", 7 => "Seven", 8 => "Eight", 9 => "Nine",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

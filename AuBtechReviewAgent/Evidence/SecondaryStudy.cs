using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Included studies that are themselves reviews (a systematic review, a meta-analysis, a scoping review, a literature
/// survey) are secondary studies: they summarise other studies and add no new evidence of their own. In a real run a
/// systematic review alone carried the answer to RQ1. The class is decided in code, from the study type the extraction
/// gave and the title, so Table A.2 can label them, the methods can name them and an answer that rests on them is marked.
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

    /// <summary>The kind of secondary study ("systematic review", "meta-analysis", ...), or null for a primary study.</summary>
    public static string? KindOf(string? studyType, string? title)
    {
        foreach (var (pattern, kind) in Kinds)
            if (pattern.IsMatch(studyType ?? "") || pattern.IsMatch(title ?? "")) return kind;
        return SurveyTitle.IsMatch(title ?? "") ? "survey" : null;
    }

    /// <summary>The kind for one extraction, or null when it is primary or nothing was extracted from it.</summary>
    public static string? KindOf(StudyExtraction e) => e.Error != null ? null : KindOf(e.StudyType, e.Title);

    /// <summary>The run's secondary studies by reference number, with their kind.</summary>
    public static SortedDictionary<int, string> In(IEnumerable<StudyExtraction>? extractions)
    {
        var result = new SortedDictionary<int, string>();
        foreach (var e in extractions ?? Enumerable.Empty<StudyExtraction>())
            if (KindOf(e) is { } kind) result.TryAdd(e.ReferenceNumber, kind);
        return result;
    }

    /// <summary>The methods sentence on the secondary studies; empty when the run included none.</summary>
    public static string MethodsSentence(IReadOnlyDictionary<int, string> secondary)
    {
        if (secondary.Count == 0) return "";
        string list = string.Join(", ", secondary.Select(s => $"{Article(s.Value)} {s.Value} [{s.Key}]"));
        bool one = secondary.Count == 1;
        return $"{(one ? "One included study is a secondary study, a review of other studies" : $"{Words(secondary.Count)} included studies are secondary studies, reviews of other studies")} ({list}). " +
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

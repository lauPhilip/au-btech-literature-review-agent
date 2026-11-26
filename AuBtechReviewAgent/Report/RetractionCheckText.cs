using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// What the report says about the retraction check, written in code from the run's check (AGENTS.md rules 2 and 3): the
/// methods sentence, and the note under a study's title in Table A.1 for a correction, an expression of concern or a
/// preprint whose published version is registered.
/// </summary>
public static class RetractionCheckText
{
    /// <summary>"Retraction Watch" or "the publisher", for Crossref's source of a notice.</summary>
    public static string SourceName(string source) => source switch
    {
        "retraction-watch" => "Retraction Watch",
        "publisher" => "the publisher",
        _ => source,
    };

    /// <summary>
    /// The note for one included study in Table A.1 and on the page, such as "Correction on record (Retraction Watch,
    /// 2004-03-06)." or "Preprint: a published version is registered (https://doi.org/...)."; null when there is none.
    /// </summary>
    public static string? Note(RetractionCheckFile? check, string paperId)
    {
        var entry = check?.Studies.FirstOrDefault(e => e.PaperId == paperId);
        if (entry == null) return null;
        var parts = new List<string>();
        // A study with a retraction was excluded; one with both a concern and a correction shows the concern.
        var notice = entry.Status is RetractionStatus.Concern or RetractionStatus.Corrected
            ? entry.Notices.FirstOrDefault(n => RetractionCheck.StatusOf(new[] { n }) == entry.Status)
            : null;
        if (notice != null)
        {
            string what = notice.Type.Replace('_', ' ');
            string details = string.Join(", ", new[] { SourceName(notice.Source), notice.Date }.Where(s => s.Length > 0));
            parts.Add($"{char.ToUpperInvariant(what[0])}{what[1..]} on record{(details.Length > 0 ? $" ({details})" : "")}.");
        }
        if (entry.PublishedVersionDoi != null)
            parts.Add($"Preprint: a published version is registered (https://doi.org/{entry.PublishedVersionDoi}).");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// "Before the full texts were read, the DOIs of the 4 included studies registered at Crossref were checked in code ...",
    /// or "" when the run did not check.
    /// </summary>
    public static string Sentence(RetractionCheckFile? check)
    {
        if (check == null) return "";
        int asked = check.Studies.Count, others = Math.Max(0, check.IncludedAtCheck - asked);
        string notAsked = others == 0 ? "" : others == 1 ? " The other included study had no DOI registered at Crossref and was not checked."
            : $" The other {others} included studies had no DOI registered at Crossref and were not checked.";
        if (asked == 0)
            return "No included study had a DOI registered at Crossref, so none could be checked for retractions.";

        int retracted = Count(check, RetractionStatus.Retracted), concern = Count(check, RetractionStatus.Concern),
            corrected = Count(check, RetractionStatus.Corrected), failed = Count(check, RetractionStatus.NotChecked);
        int preprints = check.Studies.Count(e => e.PublishedVersionDoi != null && e.Status != RetractionStatus.Retracted);
        var found = new List<string>();
        if (retracted > 0) found.Add($"{retracted} had been retracted and {(retracted == 1 ? "was" : "were")} excluded, with the notice as the reason in the screening ledger");
        if (corrected > 0) found.Add($"{corrected} had a correction");
        if (concern > 0) found.Add($"{concern} had an expression of concern");
        if (preprints > 0) found.Add($"{preprints} {(preprints == 1 ? "was a preprint whose" : "were preprints whose")} published version is registered");
        bool marked = corrected + concern + preprints > 0;
        // A study whose check failed is never counted as having no notice (issue #583): "none" speaks only for the completed.
        int completed = asked - failed;
        string result = found.Count > 0
            ? $"Of {(asked == 1 ? "it" : "them")}, {Join(found)}{(marked ? "; Table A.1 marks " + (corrected + concern + preprints == 1 ? "that study" : "those studies") : "")}."
            : completed == 0 ? ""
            : completed == asked ? (asked == 1 ? "It had" : "None of them had") + " a retraction, correction or expression of concern on record."
            : (completed == 1 ? "The one study whose check was completed had no" : $"None of the {completed} studies whose check was completed had a") +
              " retraction, correction or expression of concern on record.";
        string unchecked_ = failed == 0 ? ""
            : failed == asked ? $" For {(asked == 1 ? "this study" : $"all {asked}")} the check could not be completed, so whether {(asked == 1 ? "it was" : "they were")} retracted is not known, and {(asked == 1 ? "it was" : "they were")} kept."
            : failed == 1 ? " For one study the check could not be completed, so whether it was retracted is not known, and it was kept."
            : $" For {failed} studies the check could not be completed, so whether they were retracted is not known, and they were kept.";
        string studies = asked == 1 ? "the one included study with a DOI registered at Crossref was" : $"the {asked} included studies with a DOI registered at Crossref were";
        return $"Before the full texts were read, {studies} checked in code for retractions, corrections and expressions of concern, " +
            "in Crossref's record of editorial updates, which includes the Retraction Watch database. " +
            $"{result}{unchecked_}{notAsked}".TrimStart() + $" No language model was involved, and every lookup is listed in {RetractionCheckFile.FileName}.";
    }

    private static int Count(RetractionCheckFile check, string status) => check.Studies.Count(e => e.Status == status);

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}

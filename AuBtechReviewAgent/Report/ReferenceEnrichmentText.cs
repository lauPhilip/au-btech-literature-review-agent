using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// The methods sentence on the reference enrichment, written in code from reference-enrichment.json (AGENTS.md rules 2
/// and 3): which services were asked and by what, how many references gained which fields, and that no model took part.
/// Both review types use it; a run without the file, or one that looked nothing up, gets no sentence.
/// </summary>
public static class ReferenceEnrichmentText
{
    // The fields ReferenceMerge fills, in the order the sentence names them, with their words.
    private static readonly (string Key, string Words)[] Fields =
    {
        ("authors", "the full author list"),
        ("venue", "the journal or proceedings"),
        ("volume", "the volume"),
        ("issue", "the issue"),
        ("pages", "the pages"),
        ("articleNumber", "the article number"),
        ("publisher", "the publisher"),
    };

    private static readonly string[] Services = { ReferenceAnswers.Crossref, ReferenceAnswers.OpenAlex, ReferenceAnswers.Arxiv };

    /// <summary>The run's reference-enrichment.json, or null when there is none or it cannot be read.</summary>
    public static ReferenceEnrichmentFile? Load(string runFolder)
    {
        string path = Path.Join(runFolder, ReferenceEnrichmentFile.FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ReferenceEnrichmentFile>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>
    /// "Before the references were numbered, the references of the 4 included studies with a DOI or arXiv number were looked
    /// up in code at Crossref and OpenAlex by that identifier, and 3 of them gained at least one field (2 the full author
    /// list, 3 the volume). No language model was involved: ..." or "" when nothing was looked up. <paramref name="one"/>
    /// and <paramref name="many"/> name the works, such as "included study" and "included studies".
    /// </summary>
    public static string Sentence(ReferenceEnrichmentFile? audit, string one, string many)
    {
        var looked = audit?.References.Where(r => r.Lookups.Count > 0).ToList() ?? new();
        // Studies with an identifier that were not looked up, with the reason in code (issue #566).
        var skipped = audit?.References.Where(r => r.Lookups.Count == 0 && r.Skipped != null).ToList() ?? new();
        int total = looked.Count + skipped.Count;
        if (total == 0) return "";
        string Works(int n) => n == 1 ? one : many;
        if (looked.Count == 0)
            return $"None of the {(total == 1 ? $"one {one}" : $"{total} {many}")} with a DOI or arXiv number was looked up for its reference: {SkippedReasons(skipped)}.";

        var asked = Services.Where(s => looked.Any(r => r.Lookups.Any(l => l.Service == s))).ToList();
        int gained = looked.Count(r => r.Filled.Count > 0);
        var named = Fields
            .Select(f => (f.Words, Count: looked.Count(r => r.Filled.ContainsKey(f.Key))))
            .Where(f => f.Count > 0)
            .ToList();
        int searched = looked.Count(r => r.Lookups.Any(l => l.Url.Contains("query.bibliographic=", StringComparison.Ordinal)));
        int published = looked.Count(r => r.Filled.ContainsKey("doi"));

        string works = skipped.Count > 0
            ? $"the reference{(looked.Count == 1 ? "" : "s")} of {looked.Count} of the {total} {many}"
            : looked.Count == 1 ? $"the reference of the one {one}" : $"the references of the {looked.Count} {many}";
        string result = looked.Count == 1
            ? gained == 0 ? "it gained no field" : $"it gained {Join(named.Select(f => f.Words).ToList())}"
            : gained == 0 ? "none of them gained a field"
            : $"{gained} of them gained at least one field ({string.Join(", ", named.Select(f => $"{f.Count} {f.Words}"))})";
        string byTitle = searched == 0 ? "" :
            $"; Crossref was also searched by title for the published version of {searched} {Works(searched)} from arXiv whose arXiv entry names a journal or proceedings, " +
            (published == 0 ? "and none was found" : $"and {published} {(published == 1 ? "is" : "are")} cited as that published version, with its DOI, year and venue");
        string notLooked = skipped.Count == 0 ? "" : $" The other {(skipped.Count == 1 ? "one was" : $"{skipped.Count} were")} not looked up: {SkippedReasons(skipped)}.";
        return $"Before the references were numbered, {works} with a DOI or arXiv number {(looked.Count == 1 ? "was" : "were")} " +
            $"looked up in code at {Join(asked)} by that identifier{byTitle}, and {result}.{notLooked} " +
            "No language model was involved: an answer was used only when its title named the same work, it filled gaps without " +
            "changing the year, title or venue the source gave" + (published > 0 ? " (except for a published version, which replaces the preprint)" : "") +
            $", and every lookup and filled field is listed in {ReferenceEnrichmentFile.FileName}.";
    }

    /// <summary>The reason an arXiv reference is not looked up further, as stored in reference-enrichment.json (singular); the same words as in <c>ReferenceEnricher</c>.</summary>
    public const string ArxivNoVenue = "it came from arXiv, which gives the full author list, and its arXiv entry names no journal or proceedings to complete it from";

    private const string ArxivNoVenuePlural = "they came from arXiv, which gives the full author list, and their arXiv entries name no journal or proceedings to complete them from";

    private static string SkippedReasons(IEnumerable<ReferenceEnrichmentEntry> skipped) =>
        string.Join("; ", skipped.GroupBy(r => r.Skipped!).Select(g => $"{g.Count()} because {(g.Count() == 1 ? g.Key : Plural(g.Key))}"));

    // The stored reason speaks of one reference; more than one needs "they came", not "it came".
    private static string Plural(string reason) =>
        reason == ArxivNoVenue ? ArxivNoVenuePlural
        : reason.StartsWith("it ", StringComparison.Ordinal) ? "they " + reason[3..] : reason;

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}

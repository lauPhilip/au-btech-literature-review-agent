using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// Plain-language explanations of the terms the app uses. Shown as hover and focus help wherever a term
/// appears (Components/Layout/Term.razor) and as a list on the /glossary page, so the wording is the same
/// everywhere. Keep each explanation to one or two short sentences a first-year student understands.
/// </summary>
public static class Glossary
{
    public sealed record Entry(string Key, string Term, string Explanation);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry("prisma", "PRISMA 2020",
            "A reporting standard for systematic reviews: a checklist of what the report must say and a flow diagram of how studies were found and selected."),
        new Entry("systematic-review", "Systematic review",
            "A literature review that follows a written plan, so someone else could repeat the search and selection and get the same studies."),
        new Entry("protocol", "Protocol",
            "The plan of the review (question, criteria, sources), written down and fingerprinted before anything is searched."),
        new Entry("records", "Records",
            "The search results: every title and abstract the databases returned, before anything is screened."),
        new Entry("search-strings", "Search strings",
            "The phrases sent to the databases: your query plus a few other phrasings of it, so studies that use different words are found too."),
        new Entry("saturation", "Saturation",
            "When further searches mostly find studies that earlier searches already found. It suggests more phrasings of the same question would add little."),
        new Entry("risk-of-bias", "Risk of bias",
            "How likely a study's method is to give a misleading result. Here it is shown per MMAT question, not as one overall score."),
        new Entry("screening", "Screening",
            "Deciding for each record whether it meets the inclusion criteria, based on its title and abstract."),
        new Entry("dual-screening", "Dual screening",
            "Every record is screened twice by two independent prompts. Where they disagree, the record is kept and flagged for a person to look at."),
        new Entry("kappa", "Cohen's kappa",
            "How much two screenings agree beyond what chance would give: 1 is perfect agreement, 0 is no better than chance. Above about 0.6 is usually seen as good."),
        new Entry("studies", "Included studies",
            "The records that passed screening. These are read, extracted and cited in the report."),
        new Entry("citation-chaining", "Citation chaining",
            "Also screening the papers that the included studies cite, and the papers that cite them, to find work the search missed."),
        new Entry("full-text", "Full text",
            "The whole paper, not only the abstract. It is read when an open-access copy exists; otherwise the abstract is used."),
        new Entry("mmat", "MMAT",
            "The Mixed Methods Appraisal Tool (2018): five yes/no questions per study about the quality of its method."),
        new Entry("thematic-synthesis", "Thematic synthesis",
            "Grouping the findings of the studies into themes, and writing the results theme by theme."),
        new Entry("supported", "Supported",
            "The cited paper says what the sentence claims; the supporting quote was found word for word in the paper."),
        new Entry("partly-supported", "Partly supported",
            "The cited paper backs part of the claim, or says something close but weaker. Kept in the report and marked, so you can check it."),
        new Entry("not-supported", "Not supported",
            "The cited paper does not back the claim. Kept in the report and marked, so it is not trusted by mistake."),
        new Entry("abstract-only", "Abstract only",
            "Only the abstract of the cited paper was available, and it does not settle the claim either way."),
        new Entry("unverifiable", "Could not be verified",
            "The check could not be completed for this citation, for example because the model call failed."),
        new Entry("artifact", "Artifact",
            "A concept map, flowchart, table or list built after the analysis, only from what the review found."),
        new Entry("archive", "Run archive",
            "One zip file with everything the run did: protocol, raw search results, every decision and model call, the citation audit and the manuscript, each fingerprinted with SHA-256."),
    };

    private static readonly Dictionary<string, Entry> ByKey = All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The entry for a key, or null when there is none (a typo then shows plain text, not an error).</summary>
    public static Entry? Find(string key) => ByKey.TryGetValue(key, out var entry) ? entry : null;
}

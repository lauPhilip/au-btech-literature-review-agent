using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>One item of the quality checklist.</summary>
/// <param name="Id">The item's number in Table 7, e.g. "2.3".</param>
/// <param name="Group">Its criterion, e.g. "Methodology".</param>
/// <param name="Question">The question, in plain words (Table 7, paraphrased).</param>
/// <param name="ByCode">True when the answer is decided in code from the record, not by the model.</param>
/// <param name="QuoteForLow">
/// True for the one item asked the other way round (3.3, vested interest): a quote is needed to lower its score,
/// not to raise it.
/// </param>
public sealed record QualityItem(string Id, string Group, string Question, bool ByCode = false, bool QuoteForLow = false);

/// <summary>
/// The quality checklist for grey literature of Garousi et al. (2019), Table 7: 20 items in eight criteria (authority
/// of the producer 4, methodology 6, objectivity 4, date 1, position towards related sources 1, novelty 2, impact 1,
/// outlet type 1). Each item scores 1, 0.5 or 0 (the three-point scale the paper takes from da Silva et al.), so a
/// source scores 0–20 points; the plan's threshold decides inclusion (decision 4; the paper's example uses 10, with
/// sources at or above it included). The date, the impact and the outlet type are decided here, in code; the model
/// answers the other 17, and every point it gives rests on a quote that must be found in the page's kept text.
/// </summary>
public static class GreyQualityChecklist
{
    public static readonly IReadOnlyList<QualityItem> Items = new QualityItem[]
    {
        new("1.1", "Authority of the producer", "Is the publishing organisation reputable?"),
        new("1.2", "Authority of the producer", "Is an individual author associated with a reputable organisation?"),
        new("1.3", "Authority of the producer", "Has the author published other work in the field?"),
        new("1.4", "Authority of the producer", "Does the author have expertise in the area (for example their role or job title)?"),
        new("2.1", "Methodology", "Does the source have a clearly stated aim?"),
        new("2.2", "Methodology", "Does the source have a stated methodology?"),
        new("2.3", "Methodology", "Is the source supported by authoritative, contemporary references?"),
        new("2.4", "Methodology", "Are any limits clearly stated?"),
        new("2.5", "Methodology", "Does the work cover a specific question?"),
        new("2.6", "Methodology", "Does the work refer to a particular population or case?"),
        new("3.1", "Objectivity", "Is the work balanced in presentation?"),
        new("3.2", "Objectivity", "Are the statements as objective as possible, rather than subjective opinion?"),
        new("3.3", "Objectivity", "Is the source free of vested interest (for example a tool comparison written by one of the tool's vendors)?", QuoteForLow: true),
        new("3.4", "Objectivity", "Are the conclusions supported by the data?"),
        new("4.1", "Date", "Does the item have a clearly stated date?", ByCode: true),
        new("5.1", "Position towards related sources", "Are key related grey or formal sources linked to or discussed?"),
        new("6.1", "Novelty", "Does it enrich or add something unique to the review's topic?"),
        new("6.2", "Novelty", "Does it strengthen or refute a current position?"),
        new("7.1", "Impact", "How much impact does it have (citations, votes, stars, points, comments, views)?", ByCode: true),
        new("8.1", "Outlet type", "How much control and credibility does its outlet have?", ByCode: true),
    };

    /// <summary>The items the model answers.</summary>
    public static IReadOnlyList<QualityItem> ModelItems => Items.Where(i => !i.ByCode).ToList();

    public static int MaxPoints => Items.Count;

    /// <summary>
    /// Outlet type (8.1) from the kind of grey literature: Table 7's 1st tier (books, magazines, theses, government
    /// reports, white papers) scores 1, the 2nd tier (annual reports, news, presentations, videos, Q&amp;A sites, wikis)
    /// 0.5 and the 3rd tier (blogs, e-mails, tweets) 0. Documentation and code repositories are not in the table;
    /// documentation is placed in the 2nd tier (an organisation controls it), a repository's README in the 3rd
    /// (self-published, like a blog).
    /// </summary>
    public static (double Score, string Why) OutletTier(string kind) => kind switch
    {
        "white-papers" => (1, "1st tier: a report or white paper (high outlet control and credibility)."),
        "theses" => (1, "1st tier: a thesis (high outlet control and credibility)."),
        "documentation" => (0.5, "2nd tier: documentation published by an organisation (moderate outlet control)."),
        "qa" => (0.5, "2nd tier: a Q&A site (moderate outlet control and credibility)."),
        "news" => (0.5, "2nd tier: a news or magazine article (moderate outlet control)."),
        "talks" => (0.5, "2nd tier: a talk or video (moderate outlet control)."),
        "blogs" => (0, "3rd tier: a blog post (low outlet control and credibility)."),
        "code" => (0, "3rd tier: a code repository and its README, self-published like a blog."),
        _ => (0, "3rd tier: the kind of outlet is unknown."),
    };

    /// <summary>
    /// Impact (7.1) from the counts the search returned. The paper asks for citations, backlinks, shares, comments
    /// and views to be combined "when data are available"; each source here reports different counts, so each count
    /// has two thresholds, and the best one decides: at or above the higher scores 1, at or above the lower 0.5.
    /// With no counts the item scores 0 and says that no data was available, as in the paper.
    /// </summary>
    public static (double Score, string Why) Impact(IReadOnlyDictionary<string, long> signals)
    {
        if (signals.Count == 0) return (0, "Not available: the search returned no citations, votes, stars, points or views for this source.");
        double best = 0;
        var parts = new List<string>();
        foreach (var (name, value) in signals)
        {
            if (!ImpactThresholds.TryGetValue(name, out var t)) continue;
            double score = value >= t.High ? 1 : value >= t.Low ? 0.5 : 0;
            best = Math.Max(best, score);
            parts.Add($"{value:N0} {name}");
        }
        if (parts.Count == 0) return (0, "Not available: none of the counts the search returned measures impact.");
        return (best, $"{string.Join(", ", parts)} ({ImpactRule})");
    }

    /// <summary>The thresholds for each count: (0.5 from, 1 from).</summary>
    public static readonly IReadOnlyDictionary<string, (long Low, long High)> ImpactThresholds = new Dictionary<string, (long, long)>(StringComparer.Ordinal)
    {
        ["citations"] = (1, 10),
        ["score"] = (5, 50),
        ["views"] = (1_000, 10_000),
        ["answers"] = (3, 10),
        ["stars"] = (100, 1_000),
        ["forks"] = (20, 200),
        ["points"] = (20, 100),
        ["comments"] = (10, 50),
    };

    /// <summary>The impact rule in words, for the quality record and the methods text.</summary>
    public static string ImpactRule =>
        "0.5 point from " + string.Join(", ", ImpactThresholds.Select(t => $"{t.Value.Low:N0} {t.Key}")) +
        "; 1 point from " + string.Join(", ", ImpactThresholds.Select(t => $"{t.Value.High:N0} {t.Key}"));

    /// <summary>Date (4.1): the source states a date when the search returned one for it.</summary>
    public static (double Score, string Why) Date(DateTime? published) => published is DateTime d
        ? (1, $"Dated {d:yyyy-MM-dd}.")
        : (0, "No date was given with the source.");
}

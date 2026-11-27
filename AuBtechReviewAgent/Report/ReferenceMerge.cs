using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// Puts what the reference enrichment found into a study's screening record: it fills gaps and never overrides what the
/// source gave, except an author list that a better one replaces. The reference is then built again in code
/// (<see cref="ApaCitationBuilder"/>), and every filled field is named in <see cref="ReferenceDetails.EnrichedFrom"/>.
/// </summary>
public static class ReferenceMerge
{
    public static ScreeningLog Merge(ScreeningLog log, IReadOnlyList<ReferenceAnswer> answers, IDictionary<string, string> filled)
    {
        if (answers.Count == 0) return log;
        var authors = log.Authors ?? new List<string>();
        var details = log.Details == null ? new ReferenceDetails() : Copy(log.Details);
        string venue = log.VenueName ?? "";

        // Given and family fields (Crossref) first: they need no guess about which name is the surname.
        var candidate = answers.Where(a => a.Authors.Count > 0).OrderByDescending(a => a.StructuredNames).FirstOrDefault();
        if (candidate != null && BetterAuthors(authors, candidate)
            && ApaCitationBuilder.FormatAuthorList(candidate.Authors) != ApaCitationBuilder.FormatAuthorList(authors))
        {
            authors = candidate.Authors.ToList();
            filled["authors"] = candidate.Service;
        }
        if (venue.Length == 0 && answers.FirstOrDefault(a => a.Venue.Length > 0) is { } withVenue)
        {
            venue = withVenue.Venue;
            filled["venue"] = withVenue.Service;
        }
        // A book series is not where a chapter was published; the book is (issue #678).
        else if (ApaCitationBuilder.IsBookSeries(venue) && answers.FirstOrDefault(a => a.Details.Book != null) is { } withBook)
        {
            venue = withBook.Details.Book!;
            filled["venue"] = withBook.Service;
        }
        foreach (var a in answers)
        {
            details.Volume ??= Take("volume", a.Details.Volume, a.Service, filled);
            details.Issue ??= Take("issue", a.Details.Issue, a.Service, filled);
            details.Pages ??= Take("pages", a.Details.Pages, a.Service, filled);
            details.ArticleNumber ??= Take("articleNumber", a.Details.ArticleNumber, a.Service, filled);
            details.Publisher ??= Take("publisher", a.Details.Publisher, a.Service, filled);
            details.Book ??= Take("book", a.Details.Book, a.Service, filled);
            if (details.Editors == null && a.Details.Editors is { Count: > 0 } editors)
            {
                details.Editors = editors.ToList();
                filled["editors"] = a.Service;
            }
        }
        if (filled.Count == 0) return log;

        details.EnrichedFrom = new Dictionary<string, string>(filled);
        var paper = new AcademicPaper(log.PaperId, log.Title, "", log.Year > 0 ? $"Published: {log.Year}" : "Published: ", authors, venue, log.Doi, log.Url);
        var apa = ApaCitationBuilder.Build(paper, details);
        return log with
        {
            Authors = authors,
            VenueName = apa.VenueName,
            VenueType = apa.VenueType,
            ApaCitation = apa.Citation,
            Details = apa.Details ?? details,
        };
    }

    /// <summary>
    /// Cites the published version of an arXiv preprint (issue #566): its DOI, year, journal or proceedings and details
    /// replace the preprint's, as APA 7 cites the version of record. The arXiv number is kept in the details, and every
    /// replaced field is named in <paramref name="filled"/>.
    /// </summary>
    public static ScreeningLog Published(ScreeningLog log, ReferenceAnswer published, string doi, int year, IDictionary<string, string> filled)
    {
        var authors = log.Authors ?? new List<string>();
        if (published.Authors.Count > 0 && BetterAuthors(authors, published)) { authors = published.Authors.ToList(); filled["authors"] = published.Service; }
        foreach (var field in new[] { "doi", "venue" }) filled[field] = published.Service;
        if (year > 0) filled["year"] = published.Service;
        var details = new ReferenceDetails
        {
            Volume = published.Details.Volume, Issue = published.Details.Issue, Pages = published.Details.Pages,
            ArticleNumber = published.Details.ArticleNumber, Publisher = published.Details.Publisher,
            ArxivId = log.Details?.ArxivId ?? ReferenceDetails.ArxivIdOf(log.Doi, log.PaperId, log.Url),
        };
        foreach (var (field, value) in new[] { ("volume", details.Volume), ("issue", details.Issue), ("pages", details.Pages), ("articleNumber", details.ArticleNumber), ("publisher", details.Publisher) })
            if (value != null) filled[field] = published.Service;
        details.EnrichedFrom = new Dictionary<string, string>(filled);
        int citedYear = year > 0 ? year : log.Year;
        var paper = new AcademicPaper(log.PaperId, log.Title, "", citedYear > 0 ? $"Published: {citedYear}" : "Published: ", authors, published.Venue, doi, $"https://doi.org/{doi}");
        var apa = ApaCitationBuilder.Build(paper, details);
        return log with
        {
            Authors = authors,
            Doi = doi,
            Year = apa.Year,
            VenueName = apa.VenueName,
            VenueType = apa.VenueType,
            ApaCitation = apa.Citation,
            Details = apa.Details ?? details,
        };
    }

    // A list a source gave in part ("et al.") or not at all is replaced; otherwise only by one with at least as many names
    // whose parts are known, or by a longer one.
    private static bool BetterAuthors(IReadOnlyList<string> current, ReferenceAnswer candidate)
    {
        bool partial = current.Any(a => a.Trim().Equals(ApaCitationBuilder.EtAl, StringComparison.OrdinalIgnoreCase));
        int named = current.Count(a => !a.Trim().Equals(ApaCitationBuilder.EtAl, StringComparison.OrdinalIgnoreCase));
        return named == 0 || partial || candidate.Authors.Count > named || (candidate.StructuredNames && candidate.Authors.Count == named);
    }

    private static string? Take(string field, string? value, string service, IDictionary<string, string> filled)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        filled[field] = service;
        return value.Trim();
    }

    private static ReferenceDetails Copy(ReferenceDetails d) => new()
    {
        Volume = d.Volume, Issue = d.Issue, Pages = d.Pages, ArticleNumber = d.ArticleNumber, Publisher = d.Publisher, ArxivId = d.ArxivId,
        Book = d.Book, Editors = d.Editors?.ToList(),
    };
}

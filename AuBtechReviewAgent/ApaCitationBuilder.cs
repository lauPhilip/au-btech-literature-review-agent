using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Builds APA 7th-edition reference strings directly from the metadata a source API returned, instead
/// of asking the language model to write them. The model used to invent authors, years, page ranges and
/// DOI placeholders ("https://doi.org/XXX"); building the string deterministically means every part of a
/// reference can be traced back to a field in the source record, and missing data stays visibly missing
/// ("n.d.", no DOI) rather than being filled in.
/// </summary>
public static class ApaCitationBuilder
{
    /// <summary>
    /// The one ordering used for the run's reference list. The [n] numbers the model cites, the extraction
    /// table and the LaTeX bibliography must all be sorted with this same comparer, or the numbers drift.
    /// </summary>
    public static readonly StringComparer ReferenceOrder = StringComparer.OrdinalIgnoreCase;

    public record ApaResult(string Citation, string VenueType, string VenueName, int Year);

    // Venue strings some adapters fill in when the API gave no real venue. They are not venues, so they
    // must never be printed as one or used for the quartile lookup.
    private static readonly string[] PlaceholderVenues =
    {
        "Google Scholar Indexed Publication",
        "ResearchGate Institutional Preprint",
        "IEEE Publication",
        "Scopus Indexed Journal",
    };

    private static readonly Regex ConferencePattern =
        new(@"\b(conference|proceedings|symposium|workshop|congress)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ApaResult Build(AcademicPaper paper)
    {
        string authors = FormatAuthorList(paper.Authors ?? new List<string>());
        int year = ExtractYear(paper.PublishedDate);
        string yearText = year > 0 ? year.ToString() : "n.d.";
        string title = CleanText(paper.Title).TrimEnd('.');
        string venue = CleanVenue(paper.JournalSource);
        string link = BuildLink(paper);
        bool isArxiv = (paper.Id ?? "").Contains("arxiv.org", StringComparison.OrdinalIgnoreCase);

        string venueType;
        string venuePart;
        if (string.IsNullOrEmpty(venue))
        {
            // No venue: an arXiv record without a journal_ref is a preprint; anything else is unknown.
            venueType = "Preprints";
            venuePart = isArxiv ? "arXiv." : "";
        }
        else if (venue.Contains("Transactions", StringComparison.OrdinalIgnoreCase))
        {
            venueType = "Transactions";
            venuePart = $"*{venue}*.";
        }
        else if (ConferencePattern.IsMatch(venue))
        {
            venueType = "Conferences";
            venuePart = $"In *{venue}*.";
        }
        else
        {
            venueType = "Journals";
            venuePart = $"*{venue}*.";
        }

        // APA: when there is no author, the title moves into the author position.
        var parts = new List<string>();
        if (string.IsNullOrEmpty(authors))
        {
            parts.Add($"{title}.");
            parts.Add($"({yearText}).");
        }
        else
        {
            parts.Add($"{authors} ({yearText}).");
            parts.Add($"{title}.");
        }
        if (!string.IsNullOrEmpty(venuePart)) parts.Add(venuePart);
        if (!string.IsNullOrEmpty(link)) parts.Add(link);

        return new ApaResult(string.Join(" ", parts), venueType, venue, year);
    }

    public static int ExtractYear(string? publishedDate)
    {
        if (string.IsNullOrWhiteSpace(publishedDate)) return 0;
        var match = Regex.Match(publishedDate, @"\b(19\d{2}|20\d{2})\b");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>"Jane A. Doe" -> "Doe, J. A."; names already in "Doe, J." form are kept as they are.</summary>
    public static string FormatAuthor(string rawName)
    {
        string name = CleanText(rawName).Trim(' ', ',', '…');
        if (string.IsNullOrEmpty(name) || name.Trim('.').Length == 0) return "";
        if (name.Contains(',')) return name;

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1) return tokens[0];

        string surname = tokens[^1];
        var initials = tokens[..^1]
            .SelectMany(t => t.Split('-', StringSplitOptions.RemoveEmptyEntries).Select((p, i) => (p, i)))
            .Select(x => char.ToUpperInvariant(x.p.TrimEnd('.')[0]) + ".");
        return $"{surname}, {string.Join(" ", initials)}";
    }

    /// <summary>APA 7 author list: "A", "A, & B", "A, B, & C"; more than 20 authors -> first 19, "...", last.</summary>
    public static string FormatAuthorList(IReadOnlyList<string> rawAuthors)
    {
        var authors = rawAuthors.Select(FormatAuthor).Where(a => a.Length > 0).ToList();
        if (authors.Count == 0) return "";
        if (authors.Count == 1) return authors[0];
        if (authors.Count > 20)
            return string.Join(", ", authors.Take(19)) + ", . . . " + authors[^1];
        return string.Join(", ", authors.Take(authors.Count - 1)) + ", & " + authors[^1];
    }

    private static string BuildLink(AcademicPaper paper)
    {
        if (!string.IsNullOrWhiteSpace(paper.Doi))
        {
            string doi = Regex.Replace(paper.Doi.Trim(), @"^(https?://(dx\.)?doi\.org/|doi:)", "", RegexOptions.IgnoreCase);
            // Never print placeholder DOIs such as "XXX" - only something that looks like a real DOI.
            if (Regex.IsMatch(doi, @"^10\.\d{4,9}/\S+$")) return $"https://doi.org/{doi}";
        }
        if (!string.IsNullOrWhiteSpace(paper.Url) && Uri.TryCreate(paper.Url, UriKind.Absolute, out _))
            return paper.Url.Trim();
        return "";
    }

    private static string CleanVenue(string? venue)
    {
        string v = CleanText(venue).Trim().TrimEnd('.');
        if (v.Length == 0) return "";
        if (PlaceholderVenues.Any(p => v.Equals(p, StringComparison.OrdinalIgnoreCase))) return "";
        if (v.StartsWith("arXiv Preprint Repository", StringComparison.OrdinalIgnoreCase)) return "";
        // Scholar/ResearchGate summaries often end with the year ("Future Internet, 2025"); drop it.
        v = Regex.Replace(v, @",?\s*(19|20)\d{2}\s*$", "").Trim().TrimEnd(',');
        return v;
    }

    // Asterisks would break the italic markers used for venues; collapse stray whitespace/newlines.
    private static string CleanText(string? text) =>
        Regex.Replace((text ?? "").Replace("*", ""), @"\s+", " ").Trim();
}

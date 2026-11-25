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
        "ResearchGate Institutional Preprint", // legacy runs
        "IEEE Publication",
        "Scopus Indexed Journal",
    };

    // "Applied Sciences, Volume 14, Issue 2", "IEEE Access, vol. 12, no. 3, pp. 10-20": a venue string (arXiv's
    // journal_ref, mostly) that carries the volume, issue and pages after the journal's name.
    private static readonly Regex VolumeIssuePattern = new(
        @"^(?<name>.+?),?\s+vol(?:ume|\.)?\s*(?<vol>\d+)(?:,?\s*(?:issue|no\.?|number)\s*(?<iss>\d+))?(?:,?\s*(?:pp?\.?|pages?)\s*(?<pages>\d+(?:\s*[-\u2013]+\s*\d+)?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConferencePattern =
        new(@"\b(conference|proceedings|symposium|workshop|congress)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ApaResult Build(AcademicPaper paper)
    {
        string authors = FormatAuthorList(paper.Authors ?? new List<string>());
        int year = ExtractYear(paper.PublishedDate);
        string yearText = year > 0 ? year.ToString() : "n.d.";
        string title = CleanText(paper.Title).TrimEnd('.');
        string venue = CleanVenue(paper.JournalSource);
        // APA 7 sets volume and issue as "*Journal*, *14*(2), 1–10", not inside the italic name (issue #151).
        string locator = "";
        var volumeIssue = VolumeIssuePattern.Match(venue);
        if (volumeIssue.Success && !ConferencePattern.IsMatch(venue))
        {
            venue = volumeIssue.Groups["name"].Value.Trim().TrimEnd(',');
            locator = $", *{volumeIssue.Groups["vol"].Value}*"
                      + (volumeIssue.Groups["iss"].Success ? $"({volumeIssue.Groups["iss"].Value})" : "")
                      + (volumeIssue.Groups["pages"].Success ? ", " + Regex.Replace(volumeIssue.Groups["pages"].Value, @"\s*[-\u2013]+\s*", "\u2013") : "");
        }
        string link = BuildLink(paper);
        bool isArxiv = (paper.Id ?? "").Contains("arxiv.org", StringComparison.OrdinalIgnoreCase)
                       || (paper.Doi ?? "").Contains("10.48550/arxiv", StringComparison.OrdinalIgnoreCase);

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
            venuePart = $"*{venue}*{locator}.";
        }
        else if (ConferencePattern.IsMatch(venue))
        {
            venueType = "Conferences";
            venuePart = $"In *{venue}*.";
        }
        else
        {
            venueType = "Journals";
            venuePart = $"*{venue}*{locator}.";
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
        // A venue with only the arXiv link: the link is the preprint, not the published version, so say so.
        bool preprintLink = Regex.IsMatch(link, @"10\.48550/arxiv\.|//arxiv\.org/", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(link)) parts.Add(!string.IsNullOrEmpty(venue) && preprintLink ? $"Preprint: {link}" : link);

        return new ApaResult(string.Join(" ", parts), venueType, venue, year);
    }

    /// <summary>A run of a reference's text, set in italics or not.</summary>
    public record TextSpan(string Text, bool Italic);

    /// <summary>
    /// Splits a reference built by <see cref="Build"/> into plain and italic runs, so a web page can set
    /// "*Journal*, *14*(2)" in italics instead of printing the asterisks (issue #128). The fields of a reference
    /// never hold an asterisk (<c>CleanText</c> removes them), so each pair marks one italic run. A reference
    /// with an unpaired asterisk, from an older run, is shown plain with its asterisks removed.
    /// </summary>
    public static IReadOnlyList<TextSpan> ItalicSpans(string? citation)
    {
        string text = citation ?? "";
        if (text.Length == 0) return Array.Empty<TextSpan>();
        string[] parts = text.Split('*');
        if (parts.Length % 2 == 0) return new[] { new TextSpan(text.Replace("*", ""), false) };
        var spans = new List<TextSpan>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) spans.Add(new TextSpan(parts[i], i % 2 == 1));
        }
        return spans;
    }

    public static int ExtractYear(string? publishedDate)
    {
        if (string.IsNullOrWhiteSpace(publishedDate)) return 0;
        var match = Regex.Match(publishedDate, @"\b(19\d{2}|20\d{2})\b");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>
    /// Marks an author list a source gave only in part: Scopus's search answer names the first author alone, so its list
    /// ends with this, and the reference says "et al." instead of passing the paper off as single-authored (issue #470).
    /// </summary>
    public const string EtAl = "et al.";

    // "J.", "G", ".V", "J.P.", "J.-P.": a token of initials only, as Scopus ("Liu J.") and some repositories
    // ("Gangadharan G R") write them. Two capitals without dots ("LI") are a surname in capitals, not initials.
    private static readonly Regex InitialsToken = new(@"^(?:\.?\p{Lu}\.?|(?:\p{Lu}\.-?){2,3})$", RegexOptions.Compiled);

    // The last word of a group author ("PSBC LLM Team"), which is kept as written rather than turned into "Team, P. L.".
    private static readonly HashSet<string> GroupWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Team", "Group", "Consortium", "Collaboration", "Committee", "Initiative", "Project", "Lab", "Labs",
        "Laboratory", "Institute", "University", "Organization", "Organisation", "Association", "Society", "Inc.", "Ltd.",
    };

    /// <summary>
    /// "Jane A. Doe" -> "Doe, J. A."; "Liu J." -> "Liu, J."; names already in "Doe, J." form are kept as they are, and so
    /// is a group author ("PSBC LLM Team").
    /// </summary>
    public static string FormatAuthor(string rawName)
    {
        string name = CleanText(rawName).Trim(' ', ',', '…');
        if (string.IsNullOrEmpty(name) || name.Trim('.').Length == 0) return "";
        if (name.Equals(EtAl, StringComparison.OrdinalIgnoreCase)) return EtAl;
        if (name.Contains(','))
        {
            // "Kefallinos, Dionysios" (OpenAlex sometimes gives family, given) -> "Kefallinos, D."; "Doe, J." stays.
            var sides = name.Split(',', 2);
            string family = sides[0].Trim();
            var given = sides[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (family.Length == 0 || given.Length == 0) return name;
            return $"{family}, {Initials(given)}";
        }

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1) return tokens[0];
        if (GroupWords.Contains(tokens[^1])) return name;

        // Surname first, initials last ("Liu J.", "Gangadharan G R"): the surname is every word before the initials.
        int firstInitial = TrailingInitials(tokens);
        if (firstInitial < tokens.Length)
            return $"{string.Join(" ", tokens[..firstInitial])}, {Initials(tokens[firstInitial..])}";

        return $"{tokens[^1]}, {Initials(tokens[..^1])}";
    }

    /// <summary>
    /// Where the initials at the end of a name written surname first begin ("Liu J." -> 1), or the number of words when
    /// the name does not end in initials after a surname ("Jane A. Doe" -> 3).
    /// </summary>
    public static int TrailingInitials(IReadOnlyList<string> tokens)
    {
        int first = tokens.Count;
        while (first > 0 && InitialsToken.IsMatch(tokens[first - 1])) first--;
        return first == 0 ? tokens.Count : first;
    }

    // "Jane", "A.", "Jean-Paul", "J.P." -> "J. A. J. P. J. P."
    private static string Initials(IEnumerable<string> given) =>
        string.Join(" ", given
            .SelectMany(t => t.Split(new[] { '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(p => char.ToUpperInvariant(p[0]) + "."));

    /// <summary>
    /// APA 7 author list: "A", "A, & B", "A, B, & C"; more than 20 authors -> first 19, "...", last. A list a source gave
    /// only in part ends "A, et al."
    /// </summary>
    public static string FormatAuthorList(IReadOnlyList<string> rawAuthors)
    {
        var authors = rawAuthors.Select(FormatAuthor).Where(a => a.Length > 0).ToList();
        bool partial = authors.Remove(EtAl);
        authors.RemoveAll(a => a == EtAl);
        if (authors.Count == 0) return "";
        if (partial) return string.Join(", ", authors) + ", " + EtAl;
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
        // Only http(s): a source can return any address, and a javascript: one must never reach the reference list.
        if (PrismaReviewEngine.IsWebAddress(paper.Url?.Trim()))
            return paper.Url!.Trim();
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

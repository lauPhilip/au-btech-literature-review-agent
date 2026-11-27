using System;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>Venue strings that are not a plain journal or proceedings name.</summary>
public static partial class ApaCitationBuilder
{
    // "In Taiyu Zhang, ... 2024. Focus Agent: LLM-Powered Virtual Focus Group. In ACM International Conference on Intelligent
    // Virtual Agents (IVA '24), September 16--19, 2024, GLASGOW, United Kingdom (pp. 1-10)": a whole citation in the venue
    // field, as some arXiv journal references give it (issue #678).
    private static readonly Regex TrailingPages = new(@"\(\s*pp?\.?\s*(?<pages>\d+(?:\s*[-–]+\s*\d+)?)\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DateAndPlace = new(
        @",\s*(?:(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d|(?:19|20)\d{2}\b).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InMarker = new(@"(?:^|[.\s])In\s+", RegexOptions.Compiled);

    /// <summary>
    /// True when a venue string holds the work's own title, so it is a whole citation and not the name of a journal or
    /// proceedings. A venue that only repeats the title is a book that stands alone, which <see cref="Build"/> handles.
    /// </summary>
    public static bool HoldsCitation(string venue, string title)
    {
        string v = Comparable(venue), t = Comparable(title);
        return t.Length >= 10 && v.Length > t.Length && v.Contains(t, StringComparison.Ordinal);
    }

    /// <summary>
    /// The proceedings or book named after the last "In " of a venue that holds a whole citation, without the date, place
    /// and pages, and the pages it gave. The venue is empty when nothing follows the title, so the reference prints no
    /// venue rather than the citation a second time.
    /// </summary>
    public static (string Venue, string? Pages) VenueFromCitation(string venue, string title)
    {
        var marker = InMarker.Match(venue);
        Match? last = null;
        for (; marker.Success; marker = marker.NextMatch()) last = marker;
        if (last == null) return ("", null);
        string rest = venue[(last.Index + last.Length)..].Trim();
        if (Comparable(rest).Contains(Comparable(title), StringComparison.Ordinal)) return ("", null);
        string? pages = null;
        var p = TrailingPages.Match(rest);
        if (p.Success)
        {
            pages = ReferenceDetails.PageRange(p.Groups["pages"].Value);
            rest = rest[..p.Index];
        }
        rest = DateAndPlace.Replace(rest, "").Trim().TrimEnd(',', '.', ' ');
        return (rest, pages);
    }
}

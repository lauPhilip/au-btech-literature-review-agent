using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The gallery's search box, on the server: a report matches when every word typed is found in its title, research
/// questions, keywords (theme names) or the name of its review type. Case, accents of form and punctuation are folded
/// as in GalleryFingerprint, so "AI-tutors" finds "AI tutors". No model and no index: the gallery holds at most a few
/// dozen entries. The year and version filters (issue #542) narrow the list further, by the year the run finished and
/// by the tool's release line (0.7 for every 0.7.x build), and are plain query parameters too.
/// </summary>
public static class GallerySearch
{
    /// <summary>The longest query read; the rest is ignored, so a long address cannot make the page slow.</summary>
    public const int MaxQueryLength = 120;

    /// <summary>The query as it is searched: trimmed and cut to <see cref="MaxQueryLength"/>; null when empty.</summary>
    public static string? Clean(string? query)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0) return null;
        return q.Length <= MaxQueryLength ? q : q[..MaxQueryLength].TrimEnd();
    }

    public static bool Matches(GalleryEntry entry, string? query)
    {
        var words = GalleryFingerprint.Fold(Clean(query)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        string text = " " + GalleryFingerprint.Fold(string.Join(" ", Fields(entry))) + " ";
        return words.All(w => text.Contains(w, StringComparison.Ordinal));
    }

    public static IEnumerable<GalleryEntry> Filter(IEnumerable<GalleryEntry> entries, string? query) =>
        entries.Where(e => Matches(e, query));

    private static readonly Regex LinePattern = new(@"^(\d+\.\d+)\.\d+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex LineValue = new(@"^\d{1,3}\.\d{1,3}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The year the report's run finished.</summary>
    public static int YearOf(GalleryEntry entry) => entry.CompletedUtc.Year;

    /// <summary>The release line of the tool the report was made with: "0.7" for 0.7.0 and 0.7.1-alpha.0.3; null when unknown.</summary>
    public static string? LineOf(GalleryEntry entry) =>
        LinePattern.Match(ToolVersion.Number(entry.ToolVersion)) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>A year from the address, or null when it is missing or not a plausible year.</summary>
    public static int? CleanYear(string? year) =>
        int.TryParse(year, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int y) && y is >= 2000 and <= 2999 ? y : null;

    /// <summary>A release line from the address ("0.7"), or null when it is missing or malformed.</summary>
    public static string? CleanLine(string? line) => line != null && LineValue.IsMatch(line) ? line : null;

    /// <summary>The entries finished in <paramref name="year"/> and made with release line <paramref name="line"/>; null means any.</summary>
    public static IEnumerable<GalleryEntry> Narrow(IEnumerable<GalleryEntry> entries, int? year, string? line) =>
        entries.Where(e => (year == null || YearOf(e) == year) && (line == null || LineOf(e) == line));

    /// <summary>The years and release lines to offer, newest first, from the entries shown.</summary>
    public static IReadOnlyList<int> Years(IEnumerable<GalleryEntry> entries) =>
        entries.Select(YearOf).Distinct().OrderByDescending(y => y).ToList();

    public static IReadOnlyList<string> Lines(IEnumerable<GalleryEntry> entries) =>
        entries.Select(LineOf).OfType<string>().Distinct()
            .OrderByDescending(l => Version.TryParse(l, out var v) ? v : new Version(0, 0)).ToList();

    /// <summary>The status line under the filters: how many reports match what was asked, in words.</summary>
    public static string ResultLine(int count, string? query, int? year, string? line)
    {
        var filters = new List<string>();
        if (year != null) filters.Add($"finished in {year}");
        if (line != null) filters.Add($"made with TraceableAI {line}");
        string subject = count == 0 ? "No report matches" : $"{count} {(count == 1 ? "report matches" : "reports match")}";
        string what = query != null
            ? $" \"{query}\"" + (filters.Count > 0 ? ", " + string.Join(", ", filters) : "")
            : ": " + string.Join(", ", filters);
        string next = count > 0 ? "" : query != null ? " Try fewer or other words." : " Choose another year or version.";
        return subject + what + "." + next;
    }

    private static IEnumerable<string> Fields(GalleryEntry e) =>
        new[] { e.Title, GalleryCard.TypeLabel(e.Card) }.Concat(e.Questions).Concat(e.Keywords);
}

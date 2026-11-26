using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The gallery's search box, on the server: a report matches when every word typed is found in its title, research
/// questions, keywords (theme names) or the name of its review type. Case, accents of form and punctuation are folded
/// as in GalleryFingerprint, so "AI-tutors" finds "AI tutors". No model and no index: the gallery holds at most a few
/// dozen entries.
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

    private static IEnumerable<string> Fields(GalleryEntry e) =>
        new[] { e.Title, GalleryCard.TypeLabel(e.Card) }.Concat(e.Questions).Concat(e.Keywords);
}

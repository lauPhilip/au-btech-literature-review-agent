using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>The author part of an APA 7 reference: one name, and the list with "&amp;", "et al." and the 20-author rule.</summary>
public static partial class ApaCitationBuilder
{
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
}

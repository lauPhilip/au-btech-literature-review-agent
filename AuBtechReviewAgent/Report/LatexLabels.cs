using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The \label of a numbered heading in main.tex, so a table in the paper (the PRISMA 2020 checklist) can point to the
/// section that reports an item with \ref and LaTeX prints its number.
/// </summary>
public static class LatexLabels
{
    /// <summary>"sec:" and the heading's letters and digits in lower case, joined by hyphens: "sec:search-execution".</summary>
    public static string For(string heading) =>
        "sec:" + Regex.Replace((heading ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    /// <summary>A reference to the section with this heading, as "Sec.~\ref{...}".</summary>
    public static string Ref(string heading) => $"Sec.~\\ref{{{For(heading)}}}";

    /// <summary>
    /// The \label for a heading, or nothing when labels are off or the heading's label was given already (a theme
    /// may share a name with a method section; a second label of the same name would only draw a LaTeX warning).
    /// </summary>
    public static string Tag(string heading, HashSet<string>? used) =>
        used != null && used.Add(For(heading)) ? $"\\label{{{For(heading)}}}" : "";
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// How the in-text citations of main.tex are set: the numbers of one marker in ascending order, each number once, and a
/// run of three or more consecutive numbers as a range ("[3, 5, 4, 4]" -> "[3–5]"), as numbered styles such as
/// Vancouver do. Only the typeset paper changes: the run's files, the page's citation chips and the citation check keep
/// one number per reference, so every number can still be traced to its verdict.
/// </summary>
public static class CitationMarkers
{
    // "[1]", "[1, 2]", "[2-4]", "[2–4, 7]": a marker of reference numbers only.
    private static readonly Regex Marker = new(@"\[(\d{1,4}(?:\s*(?:,|[-–])\s*\d{1,4})*)\]", RegexOptions.Compiled);

    public static string Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Marker.Replace(text, m => Numbers(m.Groups[1].Value) is { } numbers ? $"[{Compress(numbers)}]" : m.Value);
    }

    /// <summary>The numbers of a marker with its ranges expanded, or null when it is not a list of reference numbers.</summary>
    private static List<int>? Numbers(string inside)
    {
        var numbers = new List<int>();
        foreach (string part in inside.Split(','))
        {
            var ends = part.Split(new[] { '-', '–' }).Select(p => int.Parse(p.Trim())).ToArray();
            if (ends.Any(n => n < 1)) return null;
            if (ends.Length == 1) numbers.Add(ends[0]);
            else if (ends.Length == 2 && ends[0] < ends[1] && ends[1] - ends[0] <= 100) numbers.AddRange(Enumerable.Range(ends[0], ends[1] - ends[0] + 1));
            else return null;
        }
        return numbers.Distinct().OrderBy(n => n).ToList();
    }

    private static string Compress(List<int> numbers)
    {
        var parts = new List<string>();
        for (int i = 0; i < numbers.Count;)
        {
            int j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            if (j - i >= 2) parts.Add($"{numbers[i]}–{numbers[j]}");
            else for (int k = i; k <= j; k++) parts.Add(numbers[k].ToString());
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}

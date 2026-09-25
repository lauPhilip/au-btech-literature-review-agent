using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Exports the included papers as BibTeX (references.bib) and RIS (references.ris) so they can be imported
/// into Zotero, EndNote, Mendeley or a LaTeX project. Built from the same source metadata as the APA
/// references, in reference-number order.
/// </summary>
public static class BibliographyExporter
{
    public static string ToBibTeX(IEnumerable<IncludedPaperMetricRow> records)
    {
        var sb = new StringBuilder();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records.OrderBy(r => r.ReferenceNumber))
        {
            string type = r.VenueType switch
            {
                "Journals" or "Transactions" => "article",
                "Conferences" => "inproceedings",
                _ => "misc",
            };
            string key = UniqueKey(CitationKey(r), usedKeys);

            var fields = new List<(string, string)>();
            if (r.Authors.Count > 0) fields.Add(("author", string.Join(" and ", r.Authors.Select(BibAuthor))));
            fields.Add(("title", "{" + Bib(r.Title) + "}")); // double braces keep capitalisation
            if (!string.IsNullOrWhiteSpace(r.VenueName))
                fields.Add((type == "article" ? "journal" : type == "inproceedings" ? "booktitle" : "howpublished", Bib(r.VenueName)));
            else if (type == "misc" && r.Category == "Arxiv")
                fields.Add(("howpublished", "arXiv"));
            if (r.Year > 0) fields.Add(("year", r.Year.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(r.Doi)) fields.Add(("doi", r.Doi!));
            if (!string.IsNullOrWhiteSpace(r.Url)) fields.Add(("url", r.Url!));
            fields.Add(("note", Bib($"Included in TraceableAI review as reference [{r.ReferenceNumber}]")));

            sb.AppendLine($"@{type}{{{key},");
            sb.AppendLine(string.Join(",\n", fields.Select(f => $"  {f.Item1} = {{{f.Item2}}}")));
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToRis(IEnumerable<IncludedPaperMetricRow> records)
    {
        var sb = new StringBuilder();
        foreach (var r in records.OrderBy(r => r.ReferenceNumber))
        {
            string type = r.VenueType switch
            {
                "Journals" or "Transactions" => "JOUR",
                "Conferences" => "CONF",
                _ => r.Category == "Arxiv" ? "UNPB" : "GEN",
            };
            sb.AppendLine($"TY  - {type}");
            foreach (var a in r.Authors) sb.AppendLine($"AU  - {RisAuthor(a)}");
            sb.AppendLine($"TI  - {OneLine(r.Title)}");
            if (!string.IsNullOrWhiteSpace(r.VenueName)) sb.AppendLine($"T2  - {OneLine(r.VenueName)}");
            if (r.Year > 0) sb.AppendLine($"PY  - {r.Year}");
            if (!string.IsNullOrWhiteSpace(r.Doi)) sb.AppendLine($"DO  - {r.Doi}");
            if (!string.IsNullOrWhiteSpace(r.Url)) sb.AppendLine($"UR  - {r.Url}");
            if (!string.IsNullOrWhiteSpace(r.Summary)) sb.AppendLine($"N1  - {OneLine(r.Summary)}");
            sb.AppendLine($"N1  - Reference [{r.ReferenceNumber}] in the TraceableAI review");
            sb.AppendLine("ER  - ");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>surname + year + first title word, e.g. "setiawan2026llmoxie".</summary>
    public static string CitationKey(IncludedPaperMetricRow r)
    {
        string surname = r.Authors.Count > 0 ? Surname(r.Authors[0]) : "anon";
        string firstWord = Regex.Matches(r.Title ?? "", @"[\p{L}\p{N}]+")
            .Select(m => m.Value)
            .FirstOrDefault(w => w.Length > 3 && !new[] { "with", "from", "towards", "toward", "into" }.Contains(w.ToLowerInvariant()))
            ?? "paper";
        string raw = $"{surname}{(r.Year > 0 ? r.Year.ToString(CultureInfo.InvariantCulture) : "nd")}{firstWord}".ToLowerInvariant();
        return Regex.Replace(RemoveDiacritics(raw), @"[^a-z0-9]", "");
    }

    private static string UniqueKey(string key, HashSet<string> used)
    {
        string candidate = key;
        for (char suffix = 'a'; used.Contains(candidate) && suffix <= 'z'; suffix++) candidate = key + suffix;
        used.Add(candidate);
        return candidate;
    }

    private static string Surname(string author)
    {
        string a = author.Trim();
        if (a.Contains(',')) return a.Split(',')[0].Trim();
        var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : "anon";
    }

    // BibTeX and RIS both prefer "Surname, Given".
    private static string BibAuthor(string author) => Bib(RisAuthor(author));

    private static string RisAuthor(string author)
    {
        string a = Regex.Replace(author.Trim(), @"\s+", " ");
        if (a.Contains(',') || !a.Contains(' ')) return a;
        int last = a.LastIndexOf(' ');
        return $"{a[(last + 1)..]}, {a[..last]}";
    }

    private static string Bib(string? s) =>
        OneLine(s).Replace("\\", "").Replace("{", "").Replace("}", "").Replace("&", @"\&").Replace("%", @"\%").Replace("#", @"\#").Replace("_", @"\_");

    private static string OneLine(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Replace("ø", "o").Replace("æ", "ae").Replace("å", "a").Normalize(NormalizationForm.FormC);
    }
}

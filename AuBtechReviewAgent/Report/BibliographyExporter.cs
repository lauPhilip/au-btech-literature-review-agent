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
            // A list a source gave only in part ends in BibTeX's own "and others" (issue #470).
            if (r.Authors.Count > 0) fields.Add(("author", string.Join(" and ", r.Authors.Select(a => IsEtAl(a) ? "others" : BibAuthor(a)))));
            fields.Add(("title", "{" + Bib(ApaCitationBuilder.TitleAsGiven(r.Title)) + "}")); // double braces keep capitalisation; capitals only as in the reference list
            if (!string.IsNullOrWhiteSpace(r.VenueName))
                fields.Add((type == "article" ? "journal" : type == "inproceedings" ? "booktitle" : "howpublished", Bib(r.VenueName)));
            else if (type == "misc" && r.Category == "Arxiv")
                fields.Add(("howpublished", "arXiv"));
            if (r.Year > 0) fields.Add(("year", r.Year.ToString(CultureInfo.InvariantCulture)));
            var d = r.Details;
            if (d?.Volume != null) fields.Add(("volume", Bib(d.Volume)));
            if (d?.Issue != null) fields.Add(("number", Bib(d.Issue)));
            if (d?.Pages != null) fields.Add(("pages", Bib(d.Pages.Replace("\u2013", "--"))));
            else if (d?.ArticleNumber != null) fields.Add(("eid", Bib(d.ArticleNumber)));
            if (d?.Publisher != null && type == "inproceedings") fields.Add(("publisher", Bib(d.Publisher)));
            if (!string.IsNullOrWhiteSpace(r.Doi)) fields.Add(("doi", r.Doi!));
            // The database's landing page adds nothing when the DOI is there; only http(s), since a source can return any scheme.
            else if (PrismaReviewEngine.IsWebAddress(r.Url)) fields.Add(("url", r.Url!));
            if (d?.ArxivId != null)
            {
                fields.Add(("eprint", d.ArxivId));
                fields.Add(("archiveprefix", "arXiv"));
            }
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
            foreach (var a in r.Authors.Where(a => !IsEtAl(a))) sb.AppendLine($"AU  - {RisAuthor(a)}");
            sb.AppendLine($"TI  - {ApaCitationBuilder.TitleAsGiven(r.Title)}");
            if (!string.IsNullOrWhiteSpace(r.VenueName)) sb.AppendLine($"T2  - {OneLine(r.VenueName)}");
            if (r.Year > 0) sb.AppendLine($"PY  - {r.Year}");
            var d = r.Details;
            if (d?.Volume != null) sb.AppendLine($"VL  - {OneLine(d.Volume)}");
            if (d?.Issue != null) sb.AppendLine($"IS  - {OneLine(d.Issue)}");
            if (d?.Pages != null)
            {
                var (start, end) = ReferenceDetails.SplitPages(OneLine(d.Pages));
                sb.AppendLine($"SP  - {start}");
                if (end != null) sb.AppendLine($"EP  - {end}");
            }
            else if (d?.ArticleNumber != null) sb.AppendLine($"SP  - {OneLine(d.ArticleNumber)}");
            if (d?.Publisher != null) sb.AppendLine($"PB  - {OneLine(d.Publisher)}");
            if (!string.IsNullOrWhiteSpace(r.Doi)) sb.AppendLine($"DO  - {r.Doi}");
            if (!string.IsNullOrWhiteSpace(r.Doi)) sb.AppendLine($"UR  - https://doi.org/{r.Doi}");
            else if (PrismaReviewEngine.IsWebAddress(r.Url)) sb.AppendLine($"UR  - {r.Url}");
            if (d?.ArxivId != null) sb.AppendLine($"M1  - arXiv:{d.ArxivId}");
            if (!string.IsNullOrWhiteSpace(r.Summary)) sb.AppendLine($"N1  - {OneLine(r.Summary)}");
            sb.AppendLine($"N1  - Reference [{r.ReferenceNumber}] in the TraceableAI review");
            sb.AppendLine("KW  - TraceableAI");
            sb.AppendLine("KW  - included");
            sb.AppendLine("ER  - ");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every screened record as RIS, tagged for a reference manager: "included", or "excluded: reason" with the
    /// exclusion group, so a Zotero library of the whole screening can be filtered by decision. Included records
    /// carry their reference number. Records that could not be screened are tagged "not screened".
    /// </summary>
    public static string ToScreenedRis(IEnumerable<ScreeningLog> screening, IReadOnlyDictionary<string, int>? referenceNumbers = null)
    {
        var sb = new StringBuilder();
        foreach (var l in screening)
        {
            string type = l.VenueType switch
            {
                "Journals" or "Transactions" => "JOUR",
                "Conferences" => "CONF",
                _ => "GEN",
            };
            sb.AppendLine($"TY  - {type}");
            foreach (var a in (l.Authors ?? new List<string>()).Where(a => !IsEtAl(a))) sb.AppendLine($"AU  - {RisAuthor(a)}");
            sb.AppendLine($"TI  - {ApaCitationBuilder.TitleAsGiven(l.Title)}");
            if (!string.IsNullOrWhiteSpace(l.VenueName)) sb.AppendLine($"T2  - {OneLine(l.VenueName)}");
            if (l.Year > 0) sb.AppendLine($"PY  - {l.Year}");
            if (!string.IsNullOrWhiteSpace(l.Doi)) sb.AppendLine($"DO  - {l.Doi}");
            if (PrismaReviewEngine.IsWebAddress(l.Url)) sb.AppendLine($"UR  - {l.Url}");
            sb.AppendLine("KW  - TraceableAI");
            sb.AppendLine("KW  - " + l.Decision switch
            {
                "Included" => "included",
                "Excluded" => "excluded: " + ExclusionReasons.Label(l.ExclusionReason).ToLowerInvariant(),
                _ => "not screened",
            });
            if (l.HumanReviewed) sb.AppendLine("KW  - checked by reviewer");
            if (l.Decision == "Included" && referenceNumbers != null && referenceNumbers.TryGetValue(l.PaperId, out int n))
                sb.AppendLine($"N1  - Reference [{n}] in the TraceableAI review");
            if (!string.IsNullOrWhiteSpace(l.Reasoning)) sb.AppendLine($"N1  - Screening: {OneLine(l.Reasoning)}");
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
        if (parts.Length == 0) return "anon";
        int initials = ApaCitationBuilder.TrailingInitials(parts);
        return initials < parts.Length ? string.Join(" ", parts[..initials]) : parts[^1];
    }

    private static bool IsEtAl(string author) => author.Trim().Equals(ApaCitationBuilder.EtAl, StringComparison.OrdinalIgnoreCase);

    // BibTeX and RIS both prefer "Surname, Given"; a group author ("PSBC LLM Team") is braced so BibTeX keeps it whole.
    private static string BibAuthor(string author) =>
        ApaCitationBuilder.FormatAuthor(author) is var apa && apa.Contains(',') ? Bib(RisAuthor(author)) : "{" + Bib(author) + "}";

    private static string RisAuthor(string author)
    {
        string a = Regex.Replace(author.Trim(), @"\s+", " ");
        if (a.Contains(',') || !a.Contains(' ')) return a;
        // "Liu J." is already surname first: "Liu, J.", not "J., Liu".
        var parts = a.Split(' ');
        int initials = ApaCitationBuilder.TrailingInitials(parts);
        if (initials < parts.Length) return $"{string.Join(" ", parts[..initials])}, {string.Join(" ", parts[initials..])}";
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

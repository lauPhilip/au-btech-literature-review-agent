using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AuBtechReviewAgent;

/// <summary>What one bibliographic service said about one work, read from its saved answer.</summary>
public sealed class ReferenceAnswer
{
    public string Service { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>Authors as "Family, Given" when the service gives the parts (Crossref), or as the name it gives.</summary>
    public List<string> Authors { get; init; } = new();

    /// <summary>True when the names come from given and family fields, so no name order had to be guessed.</summary>
    public bool StructuredNames { get; init; }

    public string Venue { get; init; } = "";
    public ReferenceDetails Details { get; init; } = new();
}

/// <summary>
/// Reads the answers of Crossref (api.crossref.org/works/{doi}), OpenAlex (api.openalex.org/works/doi:{doi}) and the
/// arXiv API (export.arxiv.org/api/query?id_list={id}) into a <see cref="ReferenceAnswer"/>. Pure, so the saved answers
/// of a run can be read again the same way.
/// </summary>
public static class ReferenceAnswers
{
    public const string Crossref = "Crossref";
    public const string OpenAlex = "OpenAlex";
    public const string Arxiv = "arXiv";

    public static ReferenceAnswer? FromCrossref(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return null;
        var authors = new List<string>();
        bool structured = true;
        if (m.TryGetProperty("author", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
            {
                string family = Str(a, "family"), given = Str(a, "given"), name = Str(a, "name");
                if (family.Length > 0) authors.Add(given.Length > 0 ? $"{family}, {given}" : family);
                else if (name.Length > 0) { authors.Add(name); structured = false; }
            }
        string type = Str(m, "type");
        // A chapter's containers run from the series to the book ("Advances in Computational Intelligence and Robotics",
        // "Accelerating Logistics Through Generative AI, ..."), so its venue is the last one (issue #678).
        bool chapter = type == "book-chapter";
        string? book = chapter ? Null(Last(m, "container-title")) : null;
        return new ReferenceAnswer
        {
            Service = Crossref,
            Title = First(m, "title"),
            Authors = authors,
            StructuredNames = structured,
            // A posted preprint's "container" is the server, not a venue.
            Venue = type == "posted-content" ? "" : book ?? First(m, "container-title"),
            Details = new ReferenceDetails
            {
                Volume = Null(Str(m, "volume")),
                Issue = Null(Str(m, "issue")),
                Pages = ReferenceDetails.PageRange(Str(m, "page")),
                ArticleNumber = Null(Str(m, "article-number")),
                Publisher = type == "proceedings-article" || chapter ? Null(Str(m, "publisher")) : null,
                Book = book,
                Editors = chapter ? Names(m, "editor") : null,
            },
        };
    }

    public static ReferenceAnswer? FromOpenAlex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var w = doc.RootElement;
        if (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("id", out _)) return null;
        var authors = new List<string>();
        if (w.TryGetProperty("authorships", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
                if (a.TryGetProperty("author", out var author) && Str(author, "display_name") is { Length: > 0 } n) authors.Add(n);
        string venue = "", host = "";
        if (w.TryGetProperty("primary_location", out var loc) && loc.ValueKind == JsonValueKind.Object
            && loc.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
        {
            venue = Str(src, "display_name");
            host = Str(src, "host_organization_name");
            if (Str(src, "type") == "repository") venue = "";
        }
        string first = "", last = "", volume = "", issue = "";
        if (w.TryGetProperty("biblio", out var b) && b.ValueKind == JsonValueKind.Object)
            (first, last, volume, issue) = (Str(b, "first_page"), Str(b, "last_page"), Str(b, "volume"), Str(b, "issue"));
        return new ReferenceAnswer
        {
            Service = OpenAlex,
            Title = Str(w, "title") is { Length: > 0 } t ? t : Str(w, "display_name"),
            Authors = authors,
            Venue = venue,
            Details = new ReferenceDetails
            {
                Volume = Null(volume),
                Issue = Null(issue),
                Pages = first.Length == 0 ? null : last.Length == 0 || last == first ? first : $"{first}–{last}",
                Publisher = Str(w, "type") == "proceedings-article" || venue.Contains("Proceedings", StringComparison.OrdinalIgnoreCase) ? Null(host) : null,
            },
        };
    }

    public static ReferenceAnswer? FromArxiv(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var entry = XDocument.Parse(xml).Root?.Element(atom + "entry");
        string id = entry?.Element(atom + "id")?.Value ?? "";
        if (entry == null || ReferenceDetails.ArxivIdOf(id) is not { } arxivId) return null;
        return new ReferenceAnswer
        {
            Service = Arxiv,
            Title = Regex.Replace(entry.Element(atom + "title")?.Value ?? "", @"\s+", " ").Trim(),
            Authors = entry.Elements(atom + "author").Select(a => (a.Element(atom + "name")?.Value ?? "").Trim()).Where(n => n.Length > 0).ToList(),
            Details = new ReferenceDetails { ArxivId = arxivId },
        };
    }

    /// <summary>
    /// True when two titles name the same work: the same words, ignoring case and punctuation, or one with a subtitle the
    /// other leaves out. A DOI a database gave to the wrong record must not bring another work's authors.
    /// </summary>
    public static bool SameTitle(string? a, string? b)
    {
        string x = Words(a), y = Words(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || (Math.Min(x.Length, y.Length) >= 20 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)));
    }

    private static string Words(string? s) => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), "<[^>]+>", " "), @"[^\p{L}\p{N}]+", " ").Trim();

    private static string? Null(string s) => s.Length == 0 ? null : s;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static string Last(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[v.GetArrayLength() - 1].ValueKind == JsonValueKind.String
            ? Regex.Replace(v[v.GetArrayLength() - 1].GetString()!, @"\s+", " ").Trim()
            : "";

    // "Family, Given" from Crossref's structured names; null when the record names nobody.
    private static List<string>? Names(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        foreach (var a in list.EnumerateArray())
        {
            string family = Str(a, "family"), given = Str(a, "given");
            if (family.Length > 0) names.Add(given.Length > 0 ? $"{family}, {given}" : family);
        }
        return names.Count > 0 ? names : null;
    }

    private static string First(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.String
            ? Regex.Replace(v[0].GetString()!, @"\s+", " ").Trim()
            : "";
}

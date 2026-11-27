using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The parts of a reference beyond author, year, title and venue: volume, issue, pages or article number, publisher and
/// arXiv identifier. They come from the source's venue string ("IEEE Access, vol. 12, no. 3, pp. 10-20") or from the
/// reference enrichment by DOI (Crossref, OpenAlex) or arXiv ID; <see cref="EnrichedFrom"/> names every field the
/// enrichment filled and the service it came from, so each part of a reference can be traced to a saved answer.
/// </summary>
public sealed class ReferenceDetails
{
    public string? Volume { get; set; }
    public string? Issue { get; set; }

    /// <summary>A page range with an en dash ("10–20") or a single page.</summary>
    public string? Pages { get; set; }

    /// <summary>The article number of a journal that numbers articles instead of pages ("e0123", "104512").</summary>
    public string? ArticleNumber { get; set; }

    /// <summary>The publisher, printed for proceedings papers as APA 7 asks ("IEEE").</summary>
    public string? Publisher { get; set; }

    /// <summary>The title of the book a chapter is in, when a service names it apart from the book's series (issue #678).</summary>
    public string? Book { get; set; }

    /// <summary>The editors of that book as "Family, Given", when the chapter's record names them.</summary>
    public List<string>? Editors { get; set; }

    /// <summary>The arXiv identifier without its version ("2607.02703").</summary>
    public string? ArxivId { get; set; }

    /// <summary>Field name ("authors", "venue", "volume", ...) to the service that filled it ("Crossref", "OpenAlex", "arXiv").</summary>
    public Dictionary<string, string>? EnrichedFrom { get; set; }

    public bool IsEmpty => Volume == null && Issue == null && Pages == null && ArticleNumber == null && Publisher == null && ArxivId == null
        && Book == null && (Editors == null || Editors.Count == 0);

    /// <summary>These details, with every field this copy lacks taken from <paramref name="other"/>.</summary>
    public ReferenceDetails Or(ReferenceDetails? other) => other == null ? this : new ReferenceDetails
    {
        Volume = Volume ?? other.Volume,
        Issue = Issue ?? other.Issue,
        Pages = Pages ?? other.Pages,
        ArticleNumber = ArticleNumber ?? other.ArticleNumber,
        Publisher = Publisher ?? other.Publisher,
        ArxivId = ArxivId ?? other.ArxivId,
        Book = Book ?? other.Book,
        Editors = Editors ?? other.Editors,
        EnrichedFrom = EnrichedFrom ?? other.EnrichedFrom,
    };

    /// <summary>"10-20", "10 - 20", "10--20" -> "10–20"; null or blank -> null.</summary>
    public static string? PageRange(string? pages)
    {
        string p = Regex.Replace((pages ?? "").Trim(), @"\s*[-‐-—]+\s*", "–");
        return p.Length == 0 ? null : p;
    }

    /// <summary>The first and last page of a range ("10–20" -> ("10", "20")), for BibTeX and RIS.</summary>
    public static (string Start, string? End) SplitPages(string pages)
    {
        var parts = pages.Split('–', 2, StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    // "10.48550/arXiv.2607.02703", "http://arxiv.org/abs/2607.02703v1", "arXiv:2607.02703": the new-style identifier.
    private static readonly Regex ArxivPattern = new(@"(?:10\.48550/arxiv\.|arxiv\.org/(?:abs|pdf)/|arxiv:)(\d{4}\.\d{4,5}|[a-z-]+(?:\.[a-z]{2})?/\d{7})(?:v\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The arXiv identifier in a DOI, address or record id, without its version, or null.</summary>
    public static string? ArxivIdOf(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            var m = ArxivPattern.Match(c ?? "");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
}

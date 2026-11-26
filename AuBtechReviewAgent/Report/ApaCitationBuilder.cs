using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Builds APA 7th-edition reference strings directly from the metadata a source API returned, instead
/// of asking the language model to write them. The model used to invent authors, years, page ranges and
/// DOI placeholders ("https://doi.org/XXX"); building the string deterministically means every part of a
/// reference can be traced back to a field in the source record, and missing data stays visibly missing
/// ("n.d.", no DOI) rather than being filled in.
/// </summary>
public static partial class ApaCitationBuilder
{
    /// <summary>
    /// The one ordering used for the run's reference list. The [n] numbers the model cites, the extraction
    /// table and the LaTeX bibliography must all be sorted with this same comparer, or the numbers drift.
    /// It alphabetises as APA 7 does (section 9.44): letter by letter, ignoring case and accents, so "Öner" sorts with
    /// O and "Østergaard" before "Pajo", with nothing before something ("Lee, J." before "Leeds, A."). The italics
    /// markers of a title-first reference are not letters, so they are left out of the comparison.
    /// </summary>
    public static readonly StringComparer ReferenceOrder = new ApaOrder();

    private sealed class ApaOrder : StringComparer
    {
        private static readonly CompareInfo Letters = CultureInfo.InvariantCulture.CompareInfo;
        private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        private static string Plain(string? text) => (text ?? "").Replace("*", "", StringComparison.Ordinal);

        public override int Compare(string? x, string? y) => Letters.Compare(Plain(x), Plain(y), Options);
        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;
        public override int GetHashCode(string obj) => Letters.GetHashCode(Plain(obj), Options);
    }

    public record ApaResult(string Citation, string VenueType, string VenueName, int Year)
    {
        /// <summary>Volume, issue, pages, publisher and arXiv identifier, or null when the record has none.</summary>
        public ReferenceDetails? Details { get; init; }
    }

    // Venue strings some adapters fill in when the API gave no real venue. They are not venues, so they
    // must never be printed as one or used for the quartile lookup.
    private static readonly string[] PlaceholderVenues =
    {
        "Google Scholar Indexed Publication",
        "ResearchGate Institutional Preprint", // legacy runs
        "IEEE Publication",
        "Scopus Indexed Journal",
    };

    // "Applied Sciences, Volume 14, Issue 2", "IEEE Access, vol. 12, no. 3, pp. 10-20": a venue string (arXiv's
    // journal_ref, mostly) that carries the volume, issue and pages after the journal's name.
    private static readonly Regex VolumeIssuePattern = new(
        @"^(?<name>.+?),?\s+vol(?:ume|\.)?\s*(?<vol>\d+)(?:,?\s*(?:issue|no\.?|number)\s*(?<iss>\d+))?(?:,?\s*(?:pp?\.?|pages?)\s*(?<pages>\d+(?:\s*[-\u2013]+\s*\d+)?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConferencePattern =
        new(@"\b(conference|proceedings|symposium|workshop|congress)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Builds the reference. <paramref name="known"/> holds details found outside the venue string, from the reference
    /// enrichment (Crossref, OpenAlex, arXiv); they win over what the venue string carries.
    /// </summary>
    public static ApaResult Build(AcademicPaper paper, ReferenceDetails? known = null)
    {
        string authors = FormatAuthorList(paper.Authors ?? new List<string>());
        int year = ExtractYear(paper.PublishedDate);
        string yearText = year > 0 ? year.ToString() : "n.d.";
        string title = TitleAsGiven(paper.Title);
        string venue = CleanVenue(paper.JournalSource);
        // A book's record often repeats its title as the venue; the work then stands alone.
        if (Comparable(venue) == Comparable(title)) venue = "";
        var parsed = new ReferenceDetails { ArxivId = ReferenceDetails.ArxivIdOf(paper.Doi, paper.Id, paper.Url, paper.JournalSource) };
        var volumeIssue = VolumeIssuePattern.Match(venue);
        if (volumeIssue.Success && !ConferencePattern.IsMatch(venue))
        {
            venue = volumeIssue.Groups["name"].Value.Trim().TrimEnd(',');
            parsed.Volume = volumeIssue.Groups["vol"].Value;
            if (volumeIssue.Groups["iss"].Success) parsed.Issue = volumeIssue.Groups["iss"].Value;
            if (volumeIssue.Groups["pages"].Success) parsed.Pages = ReferenceDetails.PageRange(volumeIssue.Groups["pages"].Value);
        }
        var details = (known ?? new ReferenceDetails()).Or(parsed);
        string link = BuildLink(paper);

        string venueType;
        string venuePart;
        bool standAlone = string.IsNullOrEmpty(venue);
        if (standAlone)
        {
            // No venue: an arXiv record without a journal_ref is a preprint, cited with its identifier (APA 7, 10.8).
            venueType = "Preprints";
            venuePart = details.ArxivId != null ? "arXiv." : "";
        }
        else if (ConferencePattern.IsMatch(venue) && !venue.Contains("Transactions", StringComparison.OrdinalIgnoreCase))
        {
            // APA 7 (10.5): a paper in published proceedings is a chapter: "In *Proceedings* (pp. 1–5). Publisher."
            venueType = "Conferences";
            var inParens = new List<string>();
            if (details.Volume != null) inParens.Add($"Vol. {details.Volume}");
            if (details.Pages != null) inParens.Add((details.Pages.Contains('\u2013') ? "pp. " : "p. ") + details.Pages);
            else if (details.ArticleNumber != null) inParens.Add($"Article {details.ArticleNumber}");
            venuePart = $"In *{venue}*" + (inParens.Count > 0 ? $" ({string.Join(", ", inParens)})" : "") + ".";
            if (!string.IsNullOrWhiteSpace(details.Publisher) && Comparable(details.Publisher) != Comparable(venue))
                venuePart += $" {CleanText(details.Publisher).TrimEnd('.')}.";
        }
        else
        {
            // APA 7 sets volume and issue as "*Journal*, *14*(2), 1–10", not inside the italic name (issue #151).
            venueType = venue.Contains("Transactions", StringComparison.OrdinalIgnoreCase) ? "Transactions" : "Journals";
            venuePart = $"*{venue}*{JournalLocator(details)}.";
        }

        // APA 7: a work that stands alone (no journal or proceedings around it) has its title in italics, and a preprint
        // gives its number after the title.
        string titlePart = (standAlone ? $"*{title}*" : title)
                           + (standAlone && details.ArxivId != null ? $" (arXiv:{details.ArxivId})" : "");
        titlePart += title.EndsWith('?') || title.EndsWith('!') ? "" : ".";

        // APA: when there is no author, the title moves into the author position.
        var parts = new List<string>();
        if (string.IsNullOrEmpty(authors))
        {
            parts.Add(titlePart);
            parts.Add($"({yearText}).");
        }
        else
        {
            parts.Add($"{authors} ({yearText}).");
            parts.Add(titlePart);
        }
        if (!string.IsNullOrEmpty(venuePart)) parts.Add(venuePart);
        // A venue with only the arXiv link: the link is the preprint, not the published version, so say so.
        bool preprintLink = Regex.IsMatch(link, @"10\.48550/arxiv\.|//arxiv\.org/", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(link)) parts.Add(!standAlone && preprintLink ? $"Preprint: {link}" : link);

        return new ApaResult(string.Join(" ", parts), venueType, venue, year) { Details = details.IsEmpty && details.EnrichedFrom == null ? null : details };
    }

    // ", *14*(2), 1–10", ", *14*, Article 104512", or "" when there is no volume.
    private static string JournalLocator(ReferenceDetails d)
    {
        if (d.Volume == null) return d.Pages != null ? $", {d.Pages}" : d.ArticleNumber != null ? $", Article {d.ArticleNumber}" : "";
        return $", *{d.Volume}*" + (d.Issue != null ? $"({d.Issue})" : "")
               + (d.Pages != null ? $", {d.Pages}" : d.ArticleNumber != null ? $", Article {d.ArticleNumber}" : "");
    }

    private static string Comparable(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

    /// <summary>A run of a reference's text, set in italics or not.</summary>
    public record TextSpan(string Text, bool Italic);

    /// <summary>
    /// Splits a reference built by <see cref="Build"/> into plain and italic runs, so a web page can set
    /// "*Journal*, *14*(2)" in italics instead of printing the asterisks (issue #128). The fields of a reference
    /// never hold an asterisk (<c>CleanText</c> removes them), so each pair marks one italic run. A reference
    /// with an unpaired asterisk, from an older run, is shown plain with its asterisks removed.
    /// </summary>
    public static IReadOnlyList<TextSpan> ItalicSpans(string? citation)
    {
        string text = citation ?? "";
        if (text.Length == 0) return Array.Empty<TextSpan>();
        string[] parts = text.Split('*');
        if (parts.Length % 2 == 0) return new[] { new TextSpan(text.Replace("*", ""), false) };
        var spans = new List<TextSpan>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) spans.Add(new TextSpan(parts[i], i % 2 == 1));
        }
        return spans;
    }

    public static int ExtractYear(string? publishedDate)
    {
        if (string.IsNullOrWhiteSpace(publishedDate)) return 0;
        var match = Regex.Match(publishedDate, @"\b(19\d{2}|20\d{2})\b");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static string BuildLink(AcademicPaper paper)
    {
        if (!string.IsNullOrWhiteSpace(paper.Doi))
        {
            string doi = Regex.Replace(paper.Doi.Trim(), @"^(https?://(dx\.)?doi\.org/|doi:)", "", RegexOptions.IgnoreCase);
            // Never print placeholder DOIs such as "XXX" - only something that looks like a real DOI.
            if (Regex.IsMatch(doi, @"^10\.\d{4,9}/\S+$")) return $"https://doi.org/{doi}";
        }
        // Only http(s): a source can return any address, and a javascript: one must never reach the reference list.
        if (PrismaReviewEngine.IsWebAddress(paper.Url?.Trim()))
            return paper.Url!.Trim();
        return "";
    }

    private static string CleanVenue(string? venue)
    {
        string v = CleanText(venue).Trim().TrimEnd('.');
        if (v.Length == 0) return "";
        if (PlaceholderVenues.Any(p => v.Equals(p, StringComparison.OrdinalIgnoreCase))) return "";
        if (v.StartsWith("arXiv Preprint Repository", StringComparison.OrdinalIgnoreCase)) return "";
        // Scholar/ResearchGate summaries often end with the year ("Future Internet, 2025"); drop it.
        v = Regex.Replace(v, @",?\s*(19|20)\d{2}\s*$", "").Trim().TrimEnd(',');
        return v;
    }

    // Short words that are ordinary English, not acronyms, when a title comes in capitals.
    private static readonly HashSet<string> CommonShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "nor", "but", "of", "in", "on", "at", "to", "for", "by", "via", "as", "is", "are", "be",
        "it", "its", "new", "use", "how", "why", "who", "can", "do", "we", "our", "not", "all", "one", "two", "way", "key", "per",
        "off", "out", "up", "vs", "with", "from", "into",
    };

    // Proper nouns that often stand in research titles with one capital only. Sentence case lowers every other such word,
    // so a name missing here is printed in lower case; words with more capitals (GitHub, ReAct, LLMoxie) always keep theirs.
    private static readonly HashSet<string> ProperNouns = new(StringComparer.Ordinal)
    {
        "I", "Python", "Java", "Rust", "Linux", "Android", "Windows", "Excel", "Google", "Microsoft", "Amazon", "Apple", "Meta",
        "Twitter", "Reddit", "Wikipedia", "Mistral", "Claude", "Gemini", "Llama", "Bayesian", "Markov", "Monte", "Carlo",
        "Gaussian", "Boolean", "Euclidean", "Fourier", "Kalman", "Turing", "Nash", "Pareto", "Petri", "English", "European",
        "Europe", "Danish", "Denmark", "German", "Germany", "Chinese", "China", "American", "America", "African", "Africa",
        "Asian", "Asia", "Indian", "India", "British", "Scrum", "Kanban", "Kubernetes", "Docker",
        "Azure", "Unreal", "Ethereum", "Bitcoin", "Covid", "Prisma", "Lego", "Delphi",
    };

    /// <summary>
    /// The title in sentence case, without a closing full stop (issue #567): the first word and the first word after a
    /// colon, question mark or dash start with a capital, and every other word with one capital only is lowered unless it
    /// is a known proper noun. Words with more than one capital (AI, LLMs, ReAct, LLMoxie) and words with digits keep
    /// their case. A title in capitals only is lowered too, keeping words of up to three letters that are not ordinary
    /// words ("AI", "LLM"). The original title stays in the ledger and in reference-enrichment.json.
    /// </summary>
    public static string TitleAsGiven(string? raw)
    {
        string title = CleanText(raw).TrimEnd('.');
        if (!title.Any(char.IsLetter)) return title;
        bool capitals = !title.Any(char.IsLower) && title.Count(char.IsLetter) >= 12;
        if (!capitals && !title.Any(char.IsLower)) return title;
        bool startOfSentence = true;
        return Regex.Replace(title, @"[\p{L}\p{N}'’]+|[:?!.—]", m =>
        {
            string w = m.Value;
            if (w.Length == 1 && ":?!.—".Contains(w)) { startOfSentence = true; return w; }
            string word = capitals ? FromCapitals(w) : FromMixedCase(w, startOfSentence);
            if (startOfSentence && char.IsLower(word[0])) word = char.ToUpperInvariant(word[0]) + word[1..];
            startOfSentence = false;
            return word;
        });
    }

    private static string FromCapitals(string w) =>
        w.Any(char.IsDigit) || (w.Length <= 3 && !CommonShortWords.Contains(w)) ? w : w.ToLowerInvariant();

    private static string FromMixedCase(string w, bool startOfSentence)
    {
        if (startOfSentence || w.Any(char.IsDigit) || ProperNouns.Contains(w)) return w;
        bool oneCapital = char.IsUpper(w[0]) && w.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c));
        return oneCapital ? w.ToLowerInvariant() : w;
    }

    // Asterisks would break the italic markers used for venues; collapse stray whitespace/newlines.
    private static string CleanText(string? text) =>
        Regex.Replace((text ?? "").Replace("*", ""), @"\s+", " ").Trim();
}

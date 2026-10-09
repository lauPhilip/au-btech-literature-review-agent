using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// One grey source as a reference, built in code from its search record and the snapshot of its page (MLR G-6):
/// who produced it, when, its title, the site, its address, when its text was kept, and the Wayback Machine capture
/// nearest that date. <see cref="Number"/> is the source's number in the source repository, the same number its
/// findings carry (finding F3.2 comes from source 3), so a citation [3] in the paper points here.
/// </summary>
public sealed record GreyReference(
    int Number,
    string Address,
    string Producer,
    int? Year,
    string Title,
    string Site,
    string Url,
    string Kind,
    DateTime? AccessedUtc,
    string? WaybackUrl);

/// <summary>
/// The references of a multivocal run, and their exports: references.bib and references.ris for the sources the
/// results rest on, and screened.ris for every source that was screened, tagged with its decision, as the
/// systematic review exports its studies (<see cref="BibliographyExporter"/>). Everything here is written in code
/// from the run's files; nothing comes from the model.
/// </summary>
public static class GreyReferences
{
    public const string BibFile = "references.bib";
    public const string RisFile = "references.ris";
    public const string ScreenedFile = "screened.ris";

    /// <summary>
    /// The references of the sources that were extracted, in the order of the source repository
    /// (<see cref="MultivocalReporter.SourceNumbers"/>).
    /// </summary>
    public static IReadOnlyList<GreyReference> Build(MultivocalLedger ledger, MultivocalPagesFile pages, MultivocalExtractionFile extraction)
    {
        var numbers = MultivocalReporter.SourceNumbers(extraction);
        var records = Records(ledger);
        var snapshots = pages.Pages.GroupBy(p => MultivocalSearcher.AddressKey(p.Url)).ToDictionary(g => g.Key, g => g.First());
        return extraction.Sources
            .Where(s => numbers.ContainsKey(s.Address))
            .Select(s => Reference(numbers[s.Address], s.Address, records.GetValueOrDefault(s.Address), snapshots.GetValueOrDefault(s.Address), s.Title, s.Url))
            .OrderBy(r => r.Number)
            .ToList();
    }

    private static Dictionary<string, GreyRecord> Records(MultivocalLedger ledger) =>
        ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record);

    private static GreyReference Reference(int number, string address, GreyRecord? record, PageSnapshot? page, string title, string url)
    {
        bool kept = page != null && page.NotKept == null;
        string site = OneLine(record?.Site ?? "");
        return new GreyReference(
            number,
            address,
            // A page without a named producer is credited to its site, as a reference to a web page is.
            Producer: OneLine(record?.Producer is { Length: > 0 } producer ? producer : site),
            Year: record?.Published?.Year,
            Title: OneLine(record?.Title is { Length: > 0 } t ? t : title),
            Site: site,
            Url: PrismaReviewEngine.IsWebAddress(url) ? url : PrismaReviewEngine.IsWebAddress(record?.Url) ? record!.Url : "",
            Kind: record?.Kind ?? "",
            AccessedUtc: kept ? page!.AccessedUtc : null,
            WaybackUrl: kept && PrismaReviewEngine.IsWebAddress(page!.WaybackUrl) ? page.WaybackUrl : null);
    }

    /// <summary>
    /// The reference as one line of plain text, as the paper's reference list shows it:
    /// "Producer (2025). Title. Site. https://… (accessed 2026-10-07; archived at https://web.archive.org/…)".
    /// </summary>
    public static string Line(GreyReference r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Producer.Length > 0 ? r.Producer : "Unknown producer");
        sb.Append($" ({(r.Year is int y ? y.ToString(CultureInfo.InvariantCulture) : "n.d.")}). ");
        sb.Append(EndWithStop(r.Title.Length > 0 ? r.Title : "Untitled")).Append(' ');
        if (r.Site.Length > 0 && !string.Equals(r.Site, r.Producer, StringComparison.OrdinalIgnoreCase)) sb.Append($"*{r.Site}*. ");
        if (r.Url.Length > 0) sb.Append(r.Url);
        var notes = new List<string>();
        if (r.AccessedUtc is DateTime accessed) notes.Add($"accessed {accessed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (r.WaybackUrl != null) notes.Add($"archived at {r.WaybackUrl}");
        if (notes.Count > 0) sb.Append($" ({string.Join("; ", notes)})");
        return sb.ToString().Trim();
    }

    /// <summary>The references as BibTeX, one @misc entry each, with the access date as urldate.</summary>
    public static string ToBibTeX(IEnumerable<GreyReference> references)
    {
        var sb = new StringBuilder();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in references.OrderBy(r => r.Number))
        {
            var fields = new List<(string, string)>
            {
                // Double braces: a producer is often an organisation, and BibTeX would split "Acme Labs" into a first and last name.
                ("author", "{" + Bib(r.Producer.Length > 0 ? r.Producer : "Unknown producer") + "}"),
                ("title", "{" + Bib(r.Title) + "}"),
            };
            if (r.Site.Length > 0) fields.Add(("howpublished", Bib(r.Site)));
            if (r.Year is int year) fields.Add(("year", year.ToString(CultureInfo.InvariantCulture)));
            if (r.Url.Length > 0) fields.Add(("url", r.Url));
            if (r.AccessedUtc is DateTime accessed) fields.Add(("urldate", accessed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            string note = $"Grey literature ({KindLabel(r.Kind)}); source [{r.Number}] in the TraceableAI review";
            if (r.WaybackUrl != null) note += $"; archived at {r.WaybackUrl}";
            fields.Add(("note", Bib(note)));

            sb.AppendLine($"@misc{{{UniqueKey(CitationKey(r), used)},");
            sb.AppendLine(string.Join(",\n", fields.Select(f => $"  {f.Item1} = {{{f.Item2}}}")));
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The references as RIS, one ELEC (web page) record each, with the access date in Y2.</summary>
    public static string ToRis(IEnumerable<GreyReference> references)
    {
        var sb = new StringBuilder();
        foreach (var r in references.OrderBy(r => r.Number))
        {
            AppendRis(sb, r.Producer, r.Title, r.Site, r.Year, r.Url);
            if (r.AccessedUtc is DateTime accessed) sb.AppendLine($"Y2  - {accessed.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"N1  - Source [{r.Number}] in the TraceableAI review");
            if (r.WaybackUrl != null) sb.AppendLine($"N1  - Archived at {r.WaybackUrl}");
            sb.AppendLine("KW  - TraceableAI");
            sb.AppendLine("KW  - grey literature");
            if (r.Kind.Length > 0) sb.AppendLine($"KW  - {KindLabel(r.Kind).ToLowerInvariant()}");
            sb.AppendLine("KW  - included");
            sb.AppendLine("ER  - ");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every source the run found and screened, as RIS, tagged with its decision for a reference manager:
    /// "included", "excluded: reason", "duplicate" or "not screened", plus "marked to look at" where the two
    /// screenings disagreed or were unsure and "checked by reviewer" where the reviewer decided. A source the
    /// results rest on carries its reference number.
    /// </summary>
    public static string ToScreenedRis(MultivocalLedger ledger, MultivocalScreeningFile screening, IReadOnlyDictionary<string, int>? numbers = null)
    {
        var records = Records(ledger);
        var sb = new StringBuilder();
        foreach (var s in screening.Sources)
        {
            var record = records.GetValueOrDefault(s.Address);
            string site = OneLine(record?.Site ?? "");
            string producer = record?.Producer is { Length: > 0 } p ? p : site;
            string url = PrismaReviewEngine.IsWebAddress(s.Url) ? s.Url : "";
            AppendRis(sb, OneLine(producer), OneLine(s.Title), site, record?.Published?.Year, url);
            sb.AppendLine("KW  - TraceableAI");
            sb.AppendLine("KW  - grey literature");
            if (record?.Kind is { Length: > 0 } kind) sb.AppendLine($"KW  - {KindLabel(kind).ToLowerInvariant()}");
            sb.AppendLine("KW  - " + s.Decision switch
            {
                "Included" => "included",
                "Excluded" => "excluded: " + ExclusionReasons.Label(s.ExclusionReason).ToLowerInvariant(),
                "Duplicate" => "duplicate",
                _ => "not screened",
            });
            if (s.NeedsLook) sb.AppendLine("KW  - marked to look at");
            if (s.Reviewed) sb.AppendLine("KW  - checked by reviewer");
            if (numbers != null && numbers.TryGetValue(s.Address, out int n)) sb.AppendLine($"N1  - Source [{n}] in the TraceableAI review");
            if (!string.IsNullOrWhiteSpace(s.Reasoning)) sb.AppendLine($"N1  - Screening: {OneLine(s.Reasoning)}");
            sb.AppendLine("ER  - ");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes references.bib, references.ris and screened.ris to the run folder, from the run's files, so they go
    /// into the archive. A run that has not been extracted yet gets screened.ris only.
    /// </summary>
    public static async Task SaveAsync(string folder, MultivocalLedger ledger, MultivocalPagesFile pages, MultivocalScreeningFile screening, MultivocalExtractionFile? extraction)
    {
        IReadOnlyDictionary<string, int>? numbers = null;
        if (extraction != null)
        {
            var references = Build(ledger, pages, extraction);
            await SafeFile.WriteAllTextAsync(Path.Join(folder, BibFile), ToBibTeX(references));
            await SafeFile.WriteAllTextAsync(Path.Join(folder, RisFile), ToRis(references));
            numbers = MultivocalReporter.SourceNumbers(extraction);
        }
        await SafeFile.WriteAllTextAsync(Path.Join(folder, ScreenedFile), ToScreenedRis(ledger, screening, numbers));
    }

    /// <summary>producer + year + first title word, e.g. "anthropic2025effective".</summary>
    public static string CitationKey(GreyReference r)
    {
        string producer = Regex.Matches(RemoveDiacritics(r.Producer), @"[\p{L}\p{N}]+").Select(m => m.Value).FirstOrDefault() ?? "anon";
        string firstWord = Regex.Matches(RemoveDiacritics(r.Title), @"[\p{L}\p{N}]+")
            .Select(m => m.Value)
            .FirstOrDefault(w => w.Length > 3 && !new[] { "with", "from", "towards", "toward", "into", "what", "your" }.Contains(w.ToLowerInvariant()))
            ?? "source";
        string raw = $"{producer}{(r.Year is int y ? y.ToString(CultureInfo.InvariantCulture) : "nd")}{firstWord}".ToLowerInvariant();
        return Regex.Replace(raw, @"[^a-z0-9]", "");
    }

    private static void AppendRis(StringBuilder sb, string producer, string title, string site, int? year, string url)
    {
        sb.AppendLine("TY  - ELEC");
        if (producer.Length > 0) sb.AppendLine($"AU  - {producer}");
        sb.AppendLine($"TI  - {OneLine(title)}");
        if (site.Length > 0) sb.AppendLine($"T2  - {site}");
        if (year is int y) sb.AppendLine($"PY  - {y.ToString(CultureInfo.InvariantCulture)}");
        if (url.Length > 0) sb.AppendLine($"UR  - {url}");
    }

    private static string KindLabel(string kind) => MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == kind).Label ?? kind;

    private static string UniqueKey(string key, HashSet<string> used)
    {
        string candidate = key;
        for (char suffix = 'a'; used.Contains(candidate) && suffix <= 'z'; suffix++) candidate = key + suffix;
        used.Add(candidate);
        return candidate;
    }

    private static string EndWithStop(string text) => text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') ? text : text + ".";

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>One request of the reference enrichment, as kept in reference-enrichment.json.</summary>
public sealed class ReferenceLookupLog
{
    public string Service { get; set; } = "";
    /// <summary>The address asked, without the contact e-mail or key.</summary>
    public string Url { get; set; } = "";
    /// <summary>"fetched", "saved answer reused", "not found" or "failed: ...".</summary>
    public string Status { get; set; } = "";
    public string? RawFile { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>What the enrichment did for one included study.</summary>
public sealed class ReferenceEnrichmentEntry
{
    public string PaperId { get; set; } = "";
    public string? Doi { get; set; }
    public string? ArxivId { get; set; }
    public List<ReferenceLookupLog> Lookups { get; set; } = new();
    /// <summary>Field to the service it came from; empty when nothing was filled.</summary>
    public Dictionary<string, string> Filled { get; set; } = new();
    /// <summary>Why an answer was not used, such as a title that names another work.</summary>
    public List<string> NotUsed { get; set; } = new();
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
}

/// <summary>reference-enrichment.json: every lookup of the run's references and every field it filled.</summary>
public sealed class ReferenceEnrichmentFile
{
    public const string FileName = "reference-enrichment.json";
    public List<ReferenceEnrichmentEntry> References { get; set; } = new();
}

/// <summary>
/// Completes the included studies' references where the search record left gaps: the full author list (Scopus's search
/// answer names the first author alone), names split into given and family parts rather than guessed from a string,
/// and the venue, volume, issue, pages and publisher. It asks Crossref once per DOI, OpenAlex when Crossref does not know
/// the DOI or lacks the authors or venue, and the arXiv API by arXiv number for a record from another database with an
/// arXiv DOI. Each answer is saved in SourceResponses/ with its SHA-256 and reused when the same run asks again; no model
/// is called. An answer is used only when its title names the same work, and it fills gaps: it never replaces the year,
/// the title or a venue the source gave.
/// </summary>
public sealed class ReferenceEnricher
{
    private readonly Func<string, Task<string>> _get;
    private readonly string _contactEmail;
    private readonly string _openAlexKey;

    /// <param name="get">Fetches an address; throws <see cref="HttpRequestException"/> with status 404 for an unknown work.</param>
    public ReferenceEnricher(Func<string, Task<string>> get, string contactEmail = "", string openAlexKey = "")
    {
        _get = get;
        _contactEmail = contactEmail;
        _openAlexKey = openAlexKey;
    }

    public async Task<(List<ScreeningLog> Logs, ReferenceEnrichmentFile Audit)> EnrichAsync(IReadOnlyList<ScreeningLog> included, string runFolder)
    {
        var logs = new List<ScreeningLog>();
        var audit = new ReferenceEnrichmentFile();
        foreach (var log in included)
        {
            var (enriched, entry) = await EnrichOneAsync(log, runFolder);
            logs.Add(enriched);
            if (entry.Lookups.Count > 0) audit.References.Add(entry);
        }
        return (logs, audit);
    }

    private async Task<(ScreeningLog, ReferenceEnrichmentEntry)> EnrichOneAsync(ScreeningLog log, string runFolder)
    {
        string? doi = BareDoi(log.Doi);
        string? arxivId = log.Details?.ArxivId ?? ReferenceDetails.ArxivIdOf(log.Doi, log.PaperId, log.Url);
        bool arxivDoi = doi?.StartsWith("10.48550/", StringComparison.OrdinalIgnoreCase) == true;
        var entry = new ReferenceEnrichmentEntry { PaperId = log.PaperId, Doi = doi, ArxivId = arxivId, Before = log.ApaCitation };
        var answers = new List<ReferenceAnswer>();

        if (doi != null && !arxivDoi)
        {
            // Crossref holds the DOIs its members register; a DataCite DOI (Zenodo, arXiv) is not there.
            var crossref = await AskAsync(entry, runFolder, ReferenceAnswers.Crossref, $"https://api.crossref.org/works/{EscapeDoi(doi)}",
                ReferenceAnswers.FromCrossref, url => OpenSourceHttp.WithMailto(url, _contactEmail));
            if (crossref != null) answers.Add(crossref);
            if (crossref == null || crossref.Authors.Count == 0 || crossref.Venue.Length == 0)
            {
                var openAlex = await AskAsync(entry, runFolder, ReferenceAnswers.OpenAlex, $"https://api.openalex.org/works/doi:{EscapeDoi(doi)}",
                    ReferenceAnswers.FromOpenAlex, WithOpenAlexKey);
                if (openAlex != null) answers.Add(openAlex);
            }
        }
        else if (arxivId != null && !log.PaperId.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase))
        {
            // A record from another database with an arXiv DOI: the arXiv API has its authors. A record from arXiv has them already.
            var arxiv = await AskAsync(entry, runFolder, ReferenceAnswers.Arxiv, $"https://export.arxiv.org/api/query?id_list={Uri.EscapeDataString(arxivId)}",
                ReferenceAnswers.FromArxiv, url => url);
            if (arxiv != null) answers.Add(arxiv);
        }

        var usable = new List<ReferenceAnswer>();
        foreach (var a in answers)
        {
            if (ReferenceAnswers.SameTitle(a.Title, log.Title)) usable.Add(a);
            else entry.NotUsed.Add($"{a.Service} names another title (\"{a.Title}\"), so its answer was not used.");
        }
        var merged = ReferenceMerge.Merge(log, usable, entry.Filled);
        entry.After = merged.ApaCitation;
        return (merged, entry);
    }

    private async Task<ReferenceAnswer?> AskAsync(ReferenceEnrichmentEntry entry, string runFolder, string service, string url,
        Func<string, ReferenceAnswer?> parse, Func<string, string> withCredentials)
    {
        var lookup = new ReferenceLookupLog { Service = service, Url = url };
        entry.Lookups.Add(lookup);
        string ext = service == ReferenceAnswers.Arxiv ? ".xml" : ".json";
        string name = $"reference-{service.ToLowerInvariant()}-{RunManifest.Sha256(Encoding.UTF8.GetBytes(url))[..16]}{ext}";
        string path = Path.Join(runFolder, RunArchive.RawResponsesFolder, name);
        string body;
        try
        {
            if (File.Exists(path))
            {
                body = await File.ReadAllTextAsync(path);
                lookup.Status = "saved answer reused";
            }
            else
            {
                body = await _get(withCredentials(url));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(body));
                lookup.Status = "fetched";
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            lookup.Status = "not found";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            lookup.Status = "failed: " + Regex.Replace(ex.Message, @"(mailto|api_key)=[^&\s]*", "$1=…");
            return null;
        }
        lookup.RawFile = $"{RunArchive.RawResponsesFolder}/{name}";
        lookup.Sha256 = RunManifest.Sha256(new UTF8Encoding(false).GetBytes(body));
        try { return parse(body); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Xml.XmlException)
        {
            lookup.Status += "; the answer could not be read";
            return null;
        }
    }

    private string WithOpenAlexKey(string url) =>
        string.IsNullOrWhiteSpace(_openAlexKey)
            ? OpenSourceHttp.WithMailto(url, _contactEmail)
            : OpenSourceHttp.WithMailto(url, _contactEmail) + (url.Contains('?') || !string.IsNullOrWhiteSpace(_contactEmail) ? "&" : "?") + "api_key=" + Uri.EscapeDataString(_openAlexKey.Trim());

    // A DOI keeps its slash in the path, as both APIs expect; everything else is escaped.
    private static string EscapeDoi(string doi) => Uri.EscapeDataString(doi).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"https://doi.org/10.1/x", "doi:10.1/x" or "10.1/x" -> "10.1/x"; anything that is not a DOI -> null.</summary>
    public static string? BareDoi(string? doi)
    {
        string d = Regex.Replace((doi ?? "").Trim(), @"^(https?://(dx\.)?doi\.org/|doi:)", "", RegexOptions.IgnoreCase);
        return Regex.IsMatch(d, @"^10\.\d{4,9}/\S+$") ? d : null;
    }
}

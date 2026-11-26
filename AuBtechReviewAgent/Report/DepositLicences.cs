using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// What the deposit archive may share of the sources: which raw answers come from open services, and which downloaded
/// papers carry an open licence. A paper counts as open only when an answer the run saved, from OpenAlex or Unpaywall,
/// gives it a Creative Commons or public-domain licence; nothing is looked up again, so the deposit is the same every
/// time it is built. Anything the rules do not know is left out.
/// </summary>
public sealed class DepositLicences
{
    // Services whose answers anyone may share: OpenAlex (CC0), arXiv metadata (CC0), Crossref metadata (no restrictions)
    // and Unpaywall (CC0). Seed and chaining answers come from OpenAlex.
    private static readonly string[] OpenServices = { "openalex", "arxiv", "crossref", "unpaywall" };
    private static readonly string[] OpenPrefixes = { "seed-", "chain-" };

    // Licensed databases, whose terms forbid sharing their records: checked first, so a name with both is left out.
    private static readonly string[] LicensedServices = { "scopus", "sciencedirect", "elsevier", "ieee", "xplore", "wos", "webofscience", "clarivate", "springer", "wiley", "semantic", "s2" };

    private readonly HashSet<string> _dois = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _papers = new(StringComparer.Ordinal);

    /// <summary>Downloaded papers (file names in SourcePapers/) with an open licence on file, and where it came from.</summary>
    public IReadOnlyDictionary<string, string> OpenPapers => _papers;

    /// <summary>True for a raw answer from an open service, judged by its file name in SourceResponses/.</summary>
    public static bool IsOpenAnswer(string fileName)
    {
        string name = fileName.ToLowerInvariant();
        var tokens = Regex.Split(name, "[^a-z0-9]+", RegexOptions.None, TimeSpan.FromSeconds(1)).Where(t => t.Length > 0).ToHashSet();
        if (LicensedServices.Any(tokens.Contains)) return false;
        return OpenServices.Any(tokens.Contains) || OpenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>True for a licence that lets anyone share the paper: any Creative Commons licence, or the public domain.</summary>
    public static bool IsOpenLicence(string? licence) =>
        !string.IsNullOrWhiteSpace(licence) &&
        (licence.Trim().StartsWith("cc", StringComparison.OrdinalIgnoreCase) || licence.Trim().Equals("public-domain", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads one saved answer of an open service (OpenAlex list or work, Unpaywall record) for open licences.</summary>
    public void ReadAnswer(string fileName, string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        var works = root?["results"] is JsonArray results ? results.OfType<JsonObject>() : root is JsonObject one ? new[] { one } : Enumerable.Empty<JsonObject>();
        foreach (var work in works)
        {
            string? licence = LicenceOf(work);
            if (!IsOpenLicence(licence)) continue;
            string evidence = $"{licence} (SourceResponses/{fileName})";
            if (BareDoi(Str(work, "doi")) is { } doi)
            {
                _dois.Add(doi);
                AddKeyed("", doi, evidence);
            }
            if (Str(work, "id") is { } id && id.Contains("openalex.org/", StringComparison.OrdinalIgnoreCase)) _ids.Add("OPENALEX_" + id.Split('/').Last());
            foreach (var title in new[] { Str(work, "display_name"), Str(work, "title") })
                if (!string.IsNullOrWhiteSpace(title)) _papers.TryAdd(DocumentRAGUtility.PdfFileName(title), evidence);
        }
    }

    /// <summary>
    /// Reads the run's ledger for the records whose DOI or OpenAlex id has an open licence on file, so a paper a
    /// licensed database titled differently is still found under the name its PDF was saved with.
    /// </summary>
    public void ReadLedger(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (root != null) Walk(root);
    }

    private void Walk(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item != null)
                {
                    Walk(item);
                }
            }
            return;
        }
        if (node is not JsonObject obj) return;
        string? title = Str(obj, "Title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            string? paperId = Str(obj, "PaperId");
            bool open = (BareDoi(Str(obj, "Doi")) is { } doi && _dois.Contains(doi)) || (paperId != null && _ids.Contains(paperId));
            if (open)
            {
                const string why = "open licence of its DOI on file (SourceResponses/)";
                _papers.TryAdd(DocumentRAGUtility.PdfFileName(title), why);
                if (paperId != null) AddKeyed(paperId, Str(obj, "Doi"), why);
            }
        }
        foreach (var (_, value) in obj) if (value != null) Walk(value);
    }

    /// <summary>A PDF named by its paper's key (#587), and the record beside it that says which paper it is.</summary>
    private void AddKeyed(string id, string? doi, string evidence)
    {
        _papers.TryAdd(FullTextFile.PdfName(id, doi), evidence);
        _papers.TryAdd(FullTextFile.RecordName(id, doi), evidence);
    }

    // OpenAlex gives the licence of each location; Unpaywall of its best open-access location.
    private static string? LicenceOf(JsonObject work)
    {
        foreach (var location in new[] { "best_oa_location", "primary_location" })
            if (work[location] is JsonObject loc && IsOpenLicence(Str(loc, "license"))) return Str(loc, "license");
        if (work["oa_locations"] is JsonArray locations)
            foreach (var loc in locations.OfType<JsonObject>())
                if (IsOpenLicence(Str(loc, "license"))) return Str(loc, "license");
        return null;
    }

    private static string? BareDoi(string? doi) =>
        string.IsNullOrWhiteSpace(doi) ? null : Regex.Replace(doi.Trim(), @"^https?://(dx\.)?doi\.org/", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).ToLowerInvariant();

    private static string? Str(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var v) && v is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

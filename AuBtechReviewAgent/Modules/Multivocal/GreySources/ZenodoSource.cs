using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Reports, theses, white papers and other documents on Zenodo, CERN's free open repository, through its public
/// records API. Only grey literature is kept: reports, working papers, project deliverables, theses and software
/// documentation. Journal articles and preprints belong to the formal pool; data sets and software are not written sources.
/// </summary>
public sealed class ZenodoSource : IGreySource
{
    private const string Api = "https://zenodo.org/api";
    private readonly List<string> _raw = new();

    public string Key => "zenodo";
    public string Name => "Zenodo";
    public IReadOnlyList<string> LastRawResponses => _raw;

    /// <summary>
    /// The search address. A query of several plain words is searched as a phrase: Zenodo matches words in any
    /// field, so "context engineering" unquoted also finds records whose creator is called "context" (seen in a
    /// live check). Asks for more hits than needed, because records that are not grey literature are dropped.
    /// </summary>
    public static string SearchUrl(string query, int maxResults)
    {
        string q = query.Trim();
        if (q.Contains(' ') && !q.Contains('"') && !System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(AND|OR|NOT)\b|[:()*]"))
            q = $"\"{q}\"";
        return $"{Api}/records?q={Uri.EscapeDataString(q)}&size={Math.Clamp(maxResults * 2, 2, 100)}&sort=bestmatch";
    }

    public async Task<List<GreyRecord>> SearchAsync(string query, int maxResults)
    {
        _raw.Clear();
        string json = await GreySourceHttp.GetAsync(SearchUrl(query, maxResults));
        _raw.Add(json);
        return Parse(json).Take(maxResults).ToList();
    }

    public static List<GreyRecord> Parse(string json)
    {
        var records = new List<GreyRecord>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("hits", out var outer) || !outer.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return records;
        foreach (var hit in hits.EnumerateArray())
        {
            if (!hit.TryGetProperty("metadata", out var meta)) continue;
            string kind = KindOf(meta);
            if (kind.Length == 0) continue; // software, data sets, images: not written sources
            string url = hit.TryGetProperty("links", out var links) ? GreySourceHttp.WebAddress(GreyJson.Str(links, "self_html")) : "";
            if (url.Length == 0) url = GreySourceHttp.WebAddress($"https://zenodo.org/records/{GreyJson.Raw(hit, "id")}");
            string title = GreyJson.Str(meta, "title");
            if (title.Length == 0) continue;
            string creators = meta.TryGetProperty("creators", out var c) && c.ValueKind == JsonValueKind.Array
                ? string.Join("; ", c.EnumerateArray().Select(x => GreyJson.Str(x, "name")).Where(n => n.Length > 0).Take(5))
                : "";
            records.Add(new GreyRecord(
                $"zenodo:{GreyJson.Raw(hit, "id")}",
                title,
                GreySourceHttp.PlainText(GreyJson.Str(meta, "description")),
                url,
                creators,
                "zenodo.org",
                kind,
                DateTime.TryParse(GreyJson.Str(meta, "publication_date"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null,
                GreySourceHttp.Signals()));
        }
        return records;
    }

    /// <summary>The grey literature kind of a Zenodo record, or empty for records that are not written sources.</summary>
    public static string KindOf(JsonElement meta)
    {
        if (!meta.TryGetProperty("resource_type", out var rt)) return "";
        // Older answers have type/subtype; newer ones an id such as "publication-report".
        string id = GreyJson.Str(rt, "id");
        if (id.Length == 0) id = string.Join("-", new[] { GreyJson.Str(rt, "type"), GreyJson.Str(rt, "subtype") }.Where(s => s.Length > 0));
        // Only grey literature: journal articles, conference papers, preprints and books are formal literature,
        // and data sets, software, images and slides are not written sources the review can quote.
        return id switch
        {
            "publication-report" or "publication-technicalnote" or "publication-workingpaper" or "publication-deliverable" or "publication-whitepaper" => "white-papers",
            "publication-thesis" => "theses",
            "publication-softwaredocumentation" => "documentation",
            _ => "",
        };
    }
}

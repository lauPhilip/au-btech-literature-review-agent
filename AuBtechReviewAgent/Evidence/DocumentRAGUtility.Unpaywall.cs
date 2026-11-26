using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Unpaywall, the last place a legal full text is looked for, and the file name a downloaded PDF is saved under.</summary>
public static partial class DocumentRAGUtility
{
    // DOI prefixes registered with DataCite rather than Crossref. Unpaywall only covers Crossref DOIs, so it
    // answers 404 for these every time: Zenodo, figshare, arXiv, OSF, Dryad.
    private static readonly string[] DataCitePrefixes = { "10.5281/", "10.6084/", "10.48550/", "10.31219/", "10.17605/", "10.5061/" };

    /// <summary>False for DOIs Unpaywall cannot know (DataCite DOIs such as Zenodo's), so they are not looked up.</summary>
    public static bool UnpaywallKnows(string doi) =>
        !DataCitePrefixes.Any(p => doi.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // DOIs Unpaywall did not know in this process, so the same paper is not looked up again (extraction and
    // the citation check both ask for the full text).
    private static readonly ConcurrentDictionary<string, bool> UnpaywallMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name a paper's PDF is saved under in the run folder: its title in letters and digits, at most 50 characters.</summary>
    public static string PdfFileName(string? title)
    {
        string name = Regex.Replace(title ?? "paper", @"[^a-zA-Z0-9]", "_", RegexOptions.None, TimeSpan.FromSeconds(1));
        return (name.Length > 50 ? name[..50] : name) + ".pdf";
    }

    /// <summary>Unpaywall (unpaywall.org) lists legal open-access copies of papers by DOI.</summary>
    public static async Task<string?> FindUnpaywallPdfAsync(string doi, string contactEmail) =>
        await AskUnpaywallAsync(doi, contactEmail) is { } json ? ParseUnpaywallPdfUrl(json) : null;

    /// <summary>
    /// Keeps the Unpaywall answer a PDF was found through in SourceResponses/, as every source's answer is kept: it
    /// records the paper's licence, which decides whether the deposit archive may share the PDF (Report/DepositLicences.cs).
    /// </summary>
    private static void SaveUnpaywallAnswer(string runFolder, string doi, string json)
    {
        try
        {
            string folder = Path.Join(runFolder, RunArchive.RawResponsesFolder);
            Directory.CreateDirectory(folder);
            string name = $"unpaywall-{RunManifest.Sha256(Encoding.UTF8.GetBytes(doi.Trim().ToLowerInvariant()))[..16]}.json";
            File.WriteAllText(Path.Join(folder, name), json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation("Unpaywall answer for {Doi} not kept: {Message}", doi, ex.Message);
        }
    }

    private static async Task<string?> AskUnpaywallAsync(string doi, string contactEmail)
    {
        if (UnpaywallMisses.ContainsKey(doi.Trim())) return null;
        try
        {
            return await OpenSourceHttp.GetStringAsync($"https://api.unpaywall.org/v2/{Uri.EscapeDataString(doi.Trim())}?email={Uri.EscapeDataString(contactEmail.Trim())}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Normal: Unpaywall does not have every DOI. Not worth a line in the log at the default level.
            UnpaywallMisses.TryAdd(doi.Trim(), true);
            _log.LogDebug("Unpaywall does not know {Doi}", doi);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogInformation("Unpaywall lookup for {Doi} failed: {Message}", doi, ex.Message);
            return null;
        }
    }

    public static string? ParseUnpaywallPdfUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("best_oa_location", out var best) && best.ValueKind == JsonValueKind.Object
            && best.TryGetProperty("url_for_pdf", out var pdf) && pdf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pdf.GetString()))
            return pdf.GetString();
        if (root.TryGetProperty("oa_locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
            foreach (var l in locs.EnumerateArray())
                if (l.TryGetProperty("url_for_pdf", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                    return p.GetString();
        return null;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace AuBtechReviewAgent;

public record DocumentChunk(string SourceId, int PageNumber, string Text);

/// <summary>Where a paper's full text came from (or why there is none), for the ledger.</summary>
public record FullTextResult(IReadOnlyList<DocumentChunk> Chunks, string Source, string? Url);

public static class DocumentRAGUtility
{
    /// <summary>PDFs larger than this are not downloaded (a download used to have no size limit at all).</summary>
    public const long MaxPdfBytes = 40L * 1024 * 1024;

    private static readonly HttpClient _client = CreateClient();
    private static readonly ILogger _log = AppLog.For("DocumentRAGUtility");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OpenSourceHttp.UserAgent);
        return client;
    }

    /// <summary>
    /// Finds a legal full text for the paper, downloads it and splits it into page-tagged chunks. Tried in
    /// order: arXiv; the open-access PDF link the source reported (OpenAlex, Semantic Scholar, Crossref);
    /// Unpaywall by DOI. Returns no chunks (and says why) when none is available.
    /// </summary>
    public static async Task<FullTextResult> IngestAndChunkPaperAsync(AcademicPaper paper, string targetDir, string contactEmail = "")
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return new FullTextResult(Array.Empty<DocumentChunk>(), "none", null);
        Directory.CreateDirectory(targetDir);

        string sanitizedName = Regex.Replace(paper.Title ?? "paper", @"[^a-zA-Z0-9]", "_");
        if (sanitizedName.Length > 50) sanitizedName = sanitizedName.Substring(0, 50);
        string pdfPath = Path.Join(targetDir, $"{sanitizedName}.pdf");
        string sourceId = ChunkSourceId(paper.Id);

        var candidates = new List<(string Label, string Url)>();
        if (paper.Id.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase))
            candidates.Add(("arXiv", $"https://arxiv.org/pdf/{sourceId}.pdf"));
        else if ((paper.Doi ?? "").StartsWith("10.48550/arxiv.", StringComparison.OrdinalIgnoreCase))
            candidates.Add(("arXiv", $"https://arxiv.org/pdf/{paper.Doi!.Substring("10.48550/arxiv.".Length)}.pdf"));
        if (!string.IsNullOrWhiteSpace(paper.PdfUrl)) candidates.Add(("open-access link from the source", paper.PdfUrl!));
        string? zenodoRecord = ZenodoRecordId(paper.Doi);

        string label = "none";
        string? usedUrl = null;
        if (!File.Exists(pdfPath))
        {
            foreach (var (candidateLabel, url) in candidates)
            {
                if (await TryDownloadPdfAsync(url, pdfPath)) { label = candidateLabel; usedUrl = url; break; }
            }
            if (usedUrl == null && zenodoRecord != null)
            {
                string? zenodoUrl = await FindZenodoPdfAsync(zenodoRecord);
                if (zenodoUrl != null && await TryDownloadPdfAsync(zenodoUrl, pdfPath)) { label = "Zenodo"; usedUrl = zenodoUrl; }
            }
            if (usedUrl == null && !string.IsNullOrWhiteSpace(paper.Doi) && !string.IsNullOrWhiteSpace(contactEmail) && UnpaywallKnows(paper.Doi!))
            {
                string? oaUrl = await FindUnpaywallPdfAsync(paper.Doi!, contactEmail);
                if (oaUrl != null && await TryDownloadPdfAsync(oaUrl, pdfPath)) { label = "Unpaywall"; usedUrl = oaUrl; }
            }
        }
        else
        {
            label = "already downloaded";
        }

        if (!File.Exists(pdfPath)) return new FullTextResult(Array.Empty<DocumentChunk>(), candidates.Count == 0 && string.IsNullOrWhiteSpace(contactEmail) ? "none (no open-access copy known)" : "none (no legal open-access PDF found)", null);
        return new FullTextResult(ChunkPdf(pdfPath, sourceId), label, usedUrl);
    }

    /// <summary>The id the chunks are tagged with (the last path segment of the paper id).</summary>
    public static string ChunkSourceId(string paperId)
    {
        string token = paperId.Contains('/') ? paperId.Split('/').Last() : paperId;
        return Regex.Replace(token, @"[^a-zA-Z0-9\.\-]", "_");
    }

    // DOI prefixes registered with DataCite rather than Crossref. Unpaywall only covers Crossref DOIs, so it
    // answers 404 for these every time: Zenodo, figshare, arXiv, OSF, Dryad.
    private static readonly string[] DataCitePrefixes = { "10.5281/", "10.6084/", "10.48550/", "10.31219/", "10.17605/", "10.5061/" };

    /// <summary>False for DOIs Unpaywall cannot know (DataCite DOIs such as Zenodo's), so they are not looked up.</summary>
    public static bool UnpaywallKnows(string doi) =>
        !DataCitePrefixes.Any(p => doi.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // DOIs Unpaywall did not know in this process, so the same paper is not looked up again (extraction and
    // the citation check both ask for the full text).
    private static readonly ConcurrentDictionary<string, bool> UnpaywallMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unpaywall (unpaywall.org) lists legal open-access copies of papers by DOI.</summary>
    public static async Task<string?> FindUnpaywallPdfAsync(string doi, string contactEmail)
    {
        if (UnpaywallMisses.ContainsKey(doi.Trim())) return null;
        try
        {
            string json = await OpenSourceHttp.GetStringAsync($"https://api.unpaywall.org/v2/{Uri.EscapeDataString(doi.Trim())}?email={Uri.EscapeDataString(contactEmail.Trim())}");
            return ParseUnpaywallPdfUrl(json);
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

    /// <summary>The Zenodo record number in a Zenodo DOI ("10.5281/zenodo.18448272" gives "18448272"), or null.</summary>
    public static string? ZenodoRecordId(string? doi)
    {
        if (string.IsNullOrWhiteSpace(doi)) return null;
        var match = Regex.Match(doi.Trim(), @"^10\.5281/zenodo\.(\d+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Zenodo records are usually open access, and Unpaywall does not cover them, so the PDF is found through
    /// Zenodo's own API. Only a public PDF file of the record is used.
    /// </summary>
    public static async Task<string?> FindZenodoPdfAsync(string recordId)
    {
        try
        {
            string json = await OpenSourceHttp.GetStringAsync($"https://zenodo.org/api/records/{Uri.EscapeDataString(recordId)}");
            return ParseZenodoPdfUrl(json);
        }
        catch (Exception ex)
        {
            _log.LogInformation("Zenodo lookup for record {Record} failed: {Message}", recordId, ex.Message);
            return null;
        }
    }

    /// <summary>The download link of the first PDF file in a Zenodo record, when the record is open access.</summary>
    public static string? ParseZenodoPdfUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("access_right", out var access)
            && access.ValueKind == JsonValueKind.String && !string.Equals(access.GetString(), "open", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        foreach (var file in files.EnumerateArray())
        {
            string key = file.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
            if (!key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.TryGetProperty("links", out var links) && links.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.String)
                return self.GetString();
        }
        return null;
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

    /// <summary>
    /// Streams the download with a hard size limit and keeps it only if it really is a PDF ("%PDF" header),
    /// so an HTML login page or an oversized file never reaches the PDF parser.
    /// </summary>
    public static async Task<bool> TryDownloadPdfAsync(string url, string targetPath, long maxBytes = MaxPdfBytes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return false;
        string tmp = targetPath + ".part";
        try
        {
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength is long len && len > maxBytes) return false;

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(tmp))
            {
                if (!await CopyWithLimitAsync(input, output, maxBytes)) { output.Close(); File.Delete(tmp); return false; }
            }

            if (!LooksLikePdf(tmp)) { File.Delete(tmp); return false; }
            File.Move(tmp, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogInformation("PDF download from {Host} failed: {Message}", uri.Host, ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception cleanupEx)
            {
                // A leftover .tmp file is ignored by the archive and removed with the run folder.
                _log.LogDebug("Could not delete a partial download: {Message}", cleanupEx.Message.Replace("\r", " ").Replace("\n", " "));
            }
            return false;
        }
    }

    public static async Task<bool> CopyWithLimitAsync(Stream input, Stream output, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > maxBytes) return false;
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return true;
    }

    public static bool LooksLikePdf(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[1024];
        int n = fs.Read(head, 0, head.Length);
        return Encoding.ASCII.GetString(head, 0, n).Contains("%PDF-");
    }

    /// <summary>Extracts the text page by page and splits it into 600-character chunks with 100 characters of overlap.</summary>
    public static List<DocumentChunk> ChunkPdf(string pdfPath, string sourceId)
    {
        var chunks = new List<DocumentChunk>();
        try
        {
            using PdfDocument document = PdfDocument.Open(pdfPath);
            foreach (var page in document.GetPages())
            {
                string pageText = Regex.Replace(page.Text?.Trim() ?? "", @"\s+", " ");
                if (string.IsNullOrWhiteSpace(pageText)) continue;

                const int chunkSize = 600, overlap = 100;
                for (int i = 0; i < pageText.Length; i += chunkSize - overlap)
                {
                    if (i + chunkSize > pageText.Length)
                    {
                        string finalSegment = pageText.Substring(i).Trim();
                        if (finalSegment.Length > 50) chunks.Add(new DocumentChunk(sourceId, page.Number, finalSegment));
                        break;
                    }
                    chunks.Add(new DocumentChunk(sourceId, page.Number, pageText.Substring(i, chunkSize).Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation("Could not read PDF {File}: {Message}", Path.GetFileName(pdfPath), ex.Message);
        }
        return chunks;
    }
}

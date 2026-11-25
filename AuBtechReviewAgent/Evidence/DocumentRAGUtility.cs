using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace AuBtechReviewAgent;

public record DocumentChunk(string SourceId, int PageNumber, string Text);

/// <summary>Where a paper's full text came from (or why there is none), for the ledger.</summary>
public record FullTextResult(IReadOnlyList<DocumentChunk> Chunks, string Source, string? Url)
{
    /// <summary>Runs of letters a reader of the PDF never sees, taken out of the chunks (#534).</summary>
    public int HiddenParts { get; init; }

    /// <summary>The text of those runs, for the scan.</summary>
    public string Hidden { get; init; } = "";
}

public static partial class DocumentRAGUtility
{
    private static readonly ILogger _log = AppLog.For("DocumentRAGUtility");

    /// <summary>
    /// Finds a legal full text for the paper, downloads it and splits it into page-tagged chunks. Tried in
    /// order: arXiv; the open-access PDF link the source reported (OpenAlex, Semantic Scholar, Crossref);
    /// Unpaywall by DOI. Returns no chunks (and says why) when none is available. The PDF is kept under
    /// <see cref="PdfFileName"/>; an older run's copy under its title is still read, never written.
    /// <paramref name="cancel"/> stops the downloads and the reading when the run is cancelled.
    /// </summary>
    public static async Task<FullTextResult> IngestAndChunkPaperAsync(AcademicPaper paper, string targetDir, string contactEmail = "",
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return new FullTextResult(Array.Empty<DocumentChunk>(), "none", null);
        Directory.CreateDirectory(targetDir);

        string pdfPath = Path.Join(targetDir, PdfFileName(paper));
        string legacyPath = Path.Join(targetDir, LegacyPdfFileName(paper));
        if (!File.Exists(pdfPath) && File.Exists(legacyPath)) pdfPath = legacyPath;
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
                if (await TryDownloadPdfAsync(url, pdfPath, cancel: cancel)) { label = candidateLabel; usedUrl = url; break; }
            }
            cancel.ThrowIfCancellationRequested();
            if (usedUrl == null && zenodoRecord != null)
            {
                string? zenodoUrl = await FindZenodoPdfAsync(zenodoRecord);
                if (zenodoUrl != null && await TryDownloadPdfAsync(zenodoUrl, pdfPath, cancel: cancel)) { label = "Zenodo"; usedUrl = zenodoUrl; }
            }
            cancel.ThrowIfCancellationRequested();
            if (usedUrl == null && !string.IsNullOrWhiteSpace(paper.Doi) && !string.IsNullOrWhiteSpace(contactEmail) && UnpaywallKnows(paper.Doi!))
            {
                string? oaUrl = await FindUnpaywallPdfAsync(paper.Doi!, contactEmail);
                if (oaUrl != null && await TryDownloadPdfAsync(oaUrl, pdfPath, cancel: cancel)) { label = "Unpaywall"; usedUrl = oaUrl; }
            }
        }
        else
        {
            label = "already downloaded";
        }
        if (usedUrl != null) WriteRecord(pdfPath, paper, usedUrl);

        if (!File.Exists(pdfPath)) return new FullTextResult(Array.Empty<DocumentChunk>(), candidates.Count == 0 && string.IsNullOrWhiteSpace(contactEmail) ? "none (no open-access copy known)" : "none (no legal open-access PDF found)", null);
        var pdf = ReadPdf(pdfPath, sourceId, cancel);
        return new FullTextResult(pdf.Chunks, label, usedUrl) { HiddenParts = pdf.HiddenParts, Hidden = pdf.Hidden };
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

    /// <summary>Extracts the visible text page by page and splits it into 600-character chunks with 100 characters of overlap.</summary>
    public static List<DocumentChunk> ChunkPdf(string pdfPath, string sourceId) => ReadPdf(pdfPath, sourceId).Chunks;

    /// <summary>The chunks of the text a reader sees (<see cref="PdfVisibleText"/>), and the hidden runs taken out of it.</summary>
    public static (List<DocumentChunk> Chunks, int HiddenParts, string Hidden) ReadPdf(string pdfPath, string sourceId, CancellationToken cancel = default)
    {
        var chunks = new List<DocumentChunk>();
        var hidden = new StringBuilder();
        int parts = 0;
        try
        {
            using PdfDocument document = PdfDocument.Open(pdfPath);
            foreach (var (number, visible) in PdfVisibleText.Pages(document))
            {
                cancel.ThrowIfCancellationRequested();
                parts += visible.HiddenParts;
                if (visible.Hidden.Length > 0) hidden.Append(visible.Hidden).Append('\n');
                string pageText = Regex.Replace(visible.Text.Trim(), @"\s+", " ");
                if (string.IsNullOrWhiteSpace(pageText)) continue;

                const int chunkSize = 600, overlap = 100;
                for (int i = 0; i < pageText.Length; i += chunkSize - overlap)
                {
                    if (i + chunkSize > pageText.Length)
                    {
                        string finalSegment = pageText.Substring(i).Trim();
                        if (finalSegment.Length > 50) chunks.Add(new DocumentChunk(sourceId, number, finalSegment));
                        break;
                    }
                    chunks.Add(new DocumentChunk(sourceId, number, pageText.Substring(i, chunkSize).Trim()));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            _log.LogInformation("Could not read PDF {File}: {Message}", Path.GetFileName(pdfPath), ex.Message);
        }
        return (chunks, parts, hidden.ToString());
    }
}

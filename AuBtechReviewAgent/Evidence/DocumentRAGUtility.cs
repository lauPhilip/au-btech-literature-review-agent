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
public record FullTextResult(IReadOnlyList<DocumentChunk> Chunks, string Source, string? Url);

public static partial class DocumentRAGUtility
{
    /// <summary>PDFs larger than this are not downloaded (a download used to have no size limit at all).</summary>
    public const long MaxPdfBytes = 40L * 1024 * 1024;

    private static readonly ILogger _log = AppLog.For("DocumentRAGUtility");

    /// <summary>
    /// Finds a legal full text for the paper, downloads it and splits it into page-tagged chunks. Tried in
    /// order: arXiv; the open-access PDF link the source reported (OpenAlex, Semantic Scholar, Crossref);
    /// Unpaywall by DOI. Returns no chunks (and says why) when none is available. The PDF is kept under
    /// <see cref="FullTextFile.PdfName(AcademicPaper)"/>, with a record of what it is beside it.
    /// </summary>
    public static async Task<FullTextResult> IngestAndChunkPaperAsync(AcademicPaper paper, string targetDir, string contactEmail = "")
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return new FullTextResult(Array.Empty<DocumentChunk>(), "none", null);
        Directory.CreateDirectory(targetDir);

        // Named by a key of the paper's own (#587); an older run's copy under its title is read, never written.
        string pdfPath = Path.Join(targetDir, FullTextFile.PdfName(paper));
        string legacyPath = Path.Join(targetDir, PdfFileName(paper.Title));
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
                if (await TryDownloadPdfAsync(url, pdfPath)) { label = candidateLabel; usedUrl = url; break; }
            }
            if (usedUrl == null && zenodoRecord != null)
            {
                string? zenodoUrl = await FindZenodoPdfAsync(zenodoRecord);
                if (zenodoUrl != null && await TryDownloadPdfAsync(zenodoUrl, pdfPath)) { label = "Zenodo"; usedUrl = zenodoUrl; }
            }
            if (usedUrl == null && !string.IsNullOrWhiteSpace(paper.Doi) && !string.IsNullOrWhiteSpace(contactEmail) && UnpaywallKnows(paper.Doi!))
            {
                string? answer = await AskUnpaywallAsync(paper.Doi!, contactEmail);
                string? oaUrl = answer == null ? null : ParseUnpaywallPdfUrl(answer);
                if (oaUrl != null && await TryDownloadPdfAsync(oaUrl, pdfPath))
                {
                    label = "Unpaywall";
                    usedUrl = oaUrl;
                    SaveUnpaywallAnswer(targetDir, paper.Doi!, answer!);
                }
            }
        }
        else
        {
            label = "already downloaded";
        }
        if (usedUrl != null) FullTextFile.WriteRecord(targetDir, paper, usedUrl);

        if (!File.Exists(pdfPath)) return new FullTextResult(Array.Empty<DocumentChunk>(), candidates.Count == 0 && string.IsNullOrWhiteSpace(contactEmail) ? "none (no open-access copy known)" : "none (no legal open-access PDF found)", null);
        return new FullTextResult(ChunkPdf(pdfPath, sourceId), label, usedUrl);
    }

    /// <summary>The id the chunks are tagged with (the last path segment of the paper id).</summary>
    public static string ChunkSourceId(string paperId)
    {
        string token = paperId.Contains('/') ? paperId.Split('/').Last() : paperId;
        return Regex.Replace(token, @"[^a-zA-Z0-9\.\-]", "_");
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

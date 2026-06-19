using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using UglyToad.PdfPig;

namespace AuBtechReviewAgent;

public record DocumentChunk(string SourceId, int PageNumber, string Text);

public static class DocumentRAGUtility
{
    private static readonly HttpClient _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<List<DocumentChunk>> IngestAndChunkPaperAsync(string paperUrl, string paperTitle)
    {
        var chunks = new List<DocumentChunk>();
        
        if (string.IsNullOrWhiteSpace(paperUrl)) return chunks;

        // 1. Establish clean directory targets and handle title scrubbing
        string sanitizedName = Regex.Replace(paperTitle, @"[^a-zA-Z0-9]", "_");
        if (sanitizedName.Length > 50) sanitizedName = sanitizedName.Substring(0, 50);
        
        string targetDir = Path.Combine(Directory.GetCurrentDirectory(), "PapersWorkspace");
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
        
        string pdfPath = Path.Combine(targetDir, $"{sanitizedName}.pdf");

        // Safely extract token ID without breaking on query parameters or ScienceDirect paths
        string paperIdToken = paperUrl.Contains("/") ? paperUrl.Split('/').Last() : paperUrl;
        paperIdToken = Regex.Replace(paperIdToken, @"[^a-zA-Z0-9\.\-]", "_");

        // 2. Download the PDF if it belongs to arXiv and doesn't exist locally
        if (!File.Exists(pdfPath))
        {
            try
            {
                // Only use the arXiv download endpoint if it's genuinely an arXiv resource
                if (paperUrl.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase))
                {
                    string downloadUrl = $"https://arxiv.org/pdf/{paperIdToken}.pdf";
                    byte[] fileBytes = await _client.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(pdfPath, fileBytes);
                }
                else
                {
                    // Gracefully skip full-text downloading for ScienceDirect if institutional full-text API keys aren't active
                    return chunks;
                }
            }
            catch
            {
                return chunks;
            }
        }

        // 3. Extract text page-by-page and chunk it semantically
        if (!File.Exists(pdfPath)) return chunks;

        try
        {
            using (PdfDocument document = PdfDocument.Open(pdfPath))
            {
                foreach (var page in document.GetPages())
                {
                    string pageText = page.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(pageText)) continue;

                    pageText = Regex.Replace(pageText, @"\s+", " ");

                    int chunkSize = 600;
                    int overlap = 100;
                    
                    for (int i = 0; i < pageText.Length; i += (chunkSize - overlap))
                    {
                        if (i + chunkSize > pageText.Length)
                        {
                            string finalSegment = pageText.Substring(i).Trim();
                            if (finalSegment.Length > 50)
                            {
                                chunks.Add(new DocumentChunk(paperIdToken, page.Number, finalSegment));
                            }
                            break;
                        }
                        
                        string segment = pageText.Substring(i, chunkSize).Trim();
                        chunks.Add(new DocumentChunk(paperIdToken, page.Number, segment));
                    }
                }
            }
        }
        catch
        {
            // Catch unreadable or locked formats safely
        }

        return chunks;
    } 
}
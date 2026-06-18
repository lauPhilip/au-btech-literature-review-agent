using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace AuBtechReviewAgent;

public record DocumentChunk(string SourceId, int PageNumber, string Text);

public static class DocumentRAGUtility
{
    private static readonly HttpClient _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

public static async Task<List<DocumentChunk>> IngestAndChunkPaperAsync(string paperUrl, string paperTitle)
    {
        var chunks = new List<DocumentChunk>();
        
        string sanitizedName = Regex.Replace(paperTitle, @"[^a-zA-Z0-9]", "_");
        if (sanitizedName.Length > 50) sanitizedName = sanitizedName.Substring(0, 50);
        
        string targetDir = Path.Combine(Directory.GetCurrentDirectory(), "PapersWorkspace");
  
        
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
        
        string pdfPath = Path.Combine(targetDir, $"{sanitizedName}.pdf");
        string paperIdToken = paperUrl.Split('/').Last();

        // 2. Download the PDF from the arXiv endpoint if it doesn't already exist locally
        if (!File.Exists(pdfPath))
        {
            try
            {
                string downloadUrl = $"https://arxiv.org/pdf/{paperIdToken}.pdf";
                byte[] fileBytes = await _client.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(pdfPath, fileBytes);
            }
            catch
            {
                // Fallback graceful skip if arXiv blocks the rapid automated stream
                return chunks;
            }
        }

        // 3. Extract text page-by-page and chunk it semantically
        try
        {
            using (PdfDocument document = PdfDocument.Open(pdfPath))
            {
                foreach (var page in document.GetPages())
                {
                    string pageText = page.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(pageText)) continue;

                    // Clean layout artifacts
                    pageText = Regex.Replace(pageText, @"\s+", " ");

                    // Slice the page text into ~600 character windows with a small overlap
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
            // Catch unreadable formats safely
        }

        return chunks;
    }
}
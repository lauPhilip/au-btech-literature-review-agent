using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Downloading a full text safely. The address comes from outside (a source's metadata, Unpaywall, Zenodo), so only
/// http(s) is fetched, every connection goes to a public address after the name is resolved, redirects are followed by
/// hand and checked hop by hop, and the size is capped. Each paper gets a file named by a stable key, so two papers
/// with the same title can never overwrite each other's PDF.
/// </summary>
public static partial class DocumentRAGUtility
{
    /// <summary>PDFs larger than this are not downloaded (a download used to have no size limit at all).</summary>
    public const long MaxPdfBytes = 40L * 1024 * 1024;

    /// <summary>At most this many redirects are followed for one download.</summary>
    public const int MaxRedirects = 5;

    private static readonly HttpClient _client = CreateClient();

    // PageFetcher's handler connects only to public addresses, checked after DNS, and never follows a redirect itself.
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(PageFetcher.PublicOnlyHandler()) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OpenSourceHttp.UserAgent);
        return client;
    }

    /// <summary>
    /// The PDF's file name: a hash of the paper's DOI, or of its id when it has none, so the name is the same in every
    /// run and unique to the paper. The title, the id and the DOI are kept beside it in a .json file of the same name.
    /// </summary>
    public static string PdfFileName(AcademicPaper paper)
    {
        string doi = Regex.Replace((paper.Doi ?? "").Trim(), @"^(https?://(dx\.)?doi\.org/|doi:)", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        string key = doi.Length > 0 ? "doi:" + doi.ToLowerInvariant() : "id:" + paper.Id;
        return "fulltext-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant() + ".pdf";
    }

    /// <summary>The name an older run gave the PDF: the first 50 letters and digits of the title. Read only, never written.</summary>
    public static string LegacyPdfFileName(AcademicPaper paper)
    {
        string name = Regex.Replace(paper.Title ?? "paper", @"[^a-zA-Z0-9]", "_", RegexOptions.None, TimeSpan.FromSeconds(1));
        return (name.Length > 50 ? name[..50] : name) + ".pdf";
    }

    /// <summary>Writes the readable record of a downloaded PDF beside it: the paper's id, DOI and title, and the address.</summary>
    private static void WriteRecord(string pdfPath, AcademicPaper paper, string? url)
    {
        try
        {
            File.WriteAllText(Path.ChangeExtension(pdfPath, ".json"), JsonSerializer.Serialize(new { paper.Id, paper.Doi, paper.Title, Url = url },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex)
        {
            _log.LogDebug("Could not write the record of a full text: {Message}", ex.Message.Replace("\r", " ").Replace("\n", " "));
        }
    }

    /// <summary>
    /// Whether an address may be downloaded: http or https, no user name or password in it, and not a literal address
    /// that is not public. A name is checked again when connecting, against the address it resolves to.
    /// </summary>
    public static bool IsDownloadable(string? url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return false;
        if (u.UserInfo.Length > 0 || u.IsLoopback) return false;
        if (IPAddress.TryParse(u.DnsSafeHost, out var ip) && !PageFetcher.IsPublic(ip)) return false;
        uri = u;
        return true;
    }

    /// <summary>
    /// Streams the download with a hard size limit and keeps it only if it really is a PDF ("%PDF" header), so an HTML
    /// login page or an oversized file never reaches the PDF parser. Redirects are followed by hand, at most
    /// <see cref="MaxRedirects"/>, and each new address is checked with <see cref="IsDownloadable"/>. A cancelled run
    /// stops the download and leaves no partial file. <paramref name="client"/> is for tests.
    /// </summary>
    public static async Task<bool> TryDownloadPdfAsync(string url, string targetPath, long maxBytes = MaxPdfBytes, CancellationToken cancel = default,
        HttpClient? client = null)
    {
        if (!IsDownloadable(url, out var uri)) return false;
        client ??= _client;
        string tmp = targetPath + ".part";
        try
        {
            HttpResponseMessage response;
            for (int hop = 0; ; hop++)
            {
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location) break;
                response.Dispose();
                if (hop >= MaxRedirects || !IsDownloadable(new Uri(uri, location).ToString(), out uri)) return false;
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode) return false;
                if (response.Content.Headers.ContentLength is long len && len > maxBytes) return false;

                await using (var input = await response.Content.ReadAsStreamAsync(cancel))
                await using (var output = File.Create(tmp))
                {
                    if (!await CopyWithLimitAsync(input, output, maxBytes, cancel)) return false;
                }
            }

            if (!LooksLikePdf(tmp)) return false;
            File.Move(tmp, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            _log.LogInformation("PDF download from {Host} failed: {Message}", uri.Host, ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception cleanupEx)
            {
                // A leftover .part file is ignored by the archive and removed with the run folder.
                _log.LogDebug("Could not delete a partial download: {Message}", cleanupEx.Message.Replace("\r", " ").Replace("\n", " "));
            }
        }
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/>; false when the input is longer. <paramref name="cancel"/> stops a
    /// slow body (the web page fetcher's deadline, or a cancelled run).
    /// </summary>
    public static async Task<bool> CopyWithLimitAsync(Stream input, Stream output, long maxBytes, CancellationToken cancel = default)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            total += read;
            if (total > maxBytes) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
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
}

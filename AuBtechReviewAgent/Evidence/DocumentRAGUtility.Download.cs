using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Downloading an open-access PDF safely (#588). Its address comes from outside (a database record, Unpaywall or
/// Zenodo), so it is fetched only from the public internet: every connection goes through
/// <see cref="PublicNetwork.Handler"/>, and redirects are followed by hand, at most <see cref="MaxPdfRedirects"/>,
/// each new address checked again. A cancelled run stops the download.
/// </summary>
public static partial class DocumentRAGUtility
{
    /// <summary>Redirects followed for one PDF; HttpClient on its own followed up to 50, to any address.</summary>
    public const int MaxPdfRedirects = 5;

    private static readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(PublicNetwork.Handler()) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OpenSourceHttp.UserAgent);
        return client;
    }

    /// <summary>
    /// Streams the download with a hard size limit and keeps it only if it really is a PDF ("%PDF" header),
    /// so an HTML login page or an oversized file never reaches the PDF parser. False when there is no PDF;
    /// throws only when <paramref name="cancel"/> stops it.
    /// </summary>
    /// <param name="client">For tests; by default a client that connects to public addresses only.</param>
    public static async Task<bool> TryDownloadPdfAsync(string url, string targetPath, long maxBytes = MaxPdfBytes,
        CancellationToken cancel = default, HttpClient? client = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string tmp = targetPath + ".part";
        try
        {
            for (int hop = 0; hop <= MaxPdfRedirects; hop++)
            {
                if (PublicNetwork.WhyNotAllowed(uri) is string why)
                {
                    _log.LogInformation("PDF download from {Host} refused: {Why}", Clean(uri.Host), why);
                    return false;
                }
                using var response = await (client ?? _client).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is Uri location)
                {
                    uri = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return false;
                if (response.Content.Headers.ContentLength is long len && len > maxBytes) return false;

                await using (var input = await response.Content.ReadAsStreamAsync(cancel))
                await using (var output = File.Create(tmp))
                {
                    if (!await CopyWithLimitAsync(input, output, maxBytes, cancel)) { output.Close(); File.Delete(tmp); return false; }
                }

                if (!LooksLikePdf(tmp)) { File.Delete(tmp); return false; }
                File.Move(tmp, targetPath, overwrite: true);
                return true;
            }
            _log.LogInformation("PDF download from {Host} stopped after {Max} redirects", Clean(uri.Host), MaxPdfRedirects);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            _log.LogInformation("PDF download from {Host} failed: {Message}", Clean(uri.Host), Clean(ex.Message));
            return false;
        }
        finally
        {
            DeletePartial(tmp);
        }
    }

    private static void DeletePartial(string tmp)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch (Exception cleanupEx)
        {
            // A leftover partial file is removed with the run folder.
            _log.LogDebug("Could not delete a partial download: {Message}", Clean(cleanupEx.Message));
        }
    }

    private static string Clean(string text) => text.Replace("\r", " ").Replace("\n", " ");

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

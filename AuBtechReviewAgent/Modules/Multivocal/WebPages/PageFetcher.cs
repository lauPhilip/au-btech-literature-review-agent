using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace AuBtechReviewAgent;

/// <summary>What fetching one web page gave: its text, or why it was not fetched.</summary>
/// <param name="FinalUrl">The address the text came from, after redirects.</param>
/// <param name="NotFetched">Why there is no text (robots.txt, a sign-in, the size, the type); null when there is.</param>
public sealed record FetchedPage(string Url, string FinalUrl, int? Status, string ContentType, string Title, string Text, string? NotFetched);

/// <summary>
/// Fetches web pages for their snapshots, politely and safely: robots.txt is read first and obeyed for our product
/// token (<see cref="RobotsRules"/>), one request per second per site at most (longer if the site's Crawl-delay
/// asks), nothing behind a sign-in, at most 10 MB, and only web pages, plain text and PDFs. Redirects are
/// followed one at a time, so every hop is checked the same way. Only public addresses are connected to: an
/// address that resolves to the server itself or its private network is refused, whatever the link says.
/// </summary>
public sealed class PageFetcher
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxRedirects = 5;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>A site asking for more than this between requests is not fetched at all, rather than holding the run up.</summary>
    public static readonly TimeSpan MaxCrawlDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RobotsLifetime = TimeSpan.FromHours(24);
    private static readonly Regex SignInPath = new(@"(^|/)(login|log-in|signin|sign-in|sign_in|sso|auth|oauth2?|account/login)(/|$|\?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly HttpClient _client;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly ConcurrentDictionary<string, (RobotsRules Rules, DateTime Read)> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _nextRequest = new(StringComparer.OrdinalIgnoreCase);

    static PageFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-1252 and other page charsets

    /// <param name="handler">For tests; by default a handler that connects to public addresses only.</param>
    /// <param name="delay">For tests; by default <see cref="Task.Delay(TimeSpan)"/>.</param>
    public PageFetcher(HttpMessageHandler? handler = null, Func<TimeSpan, Task>? delay = null)
    {
        _client = new HttpClient(handler ?? PublicOnlyHandler()) { Timeout = TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(OpenSourceHttp.UserAgent);
        _delay = delay ?? (wait => Task.Delay(wait));
    }

    public async Task<FetchedPage> FetchAsync(string url)
    {
        string current = url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!PrismaReviewEngine.IsWebAddress(current) || !Uri.TryCreate(current, UriKind.Absolute, out var uri))
                return NotFetched(url, current, null, "not a web address");
            if (SignInPath.IsMatch(uri.AbsolutePath))
                return NotFetched(url, current, null, "the address is a sign-in page");

            var rules = await RobotsFor(uri);
            if (!rules.Allows(uri.PathAndQuery))
                return NotFetched(url, current, null, ReferenceEquals(rules, RobotsRules.DisallowAll)
                    ? "the site's robots.txt could not be read, so nothing on the site is fetched"
                    : $"{rules.Why} does not allow it");
            if (rules.CrawlDelay > MaxCrawlDelay)
                return NotFetched(url, current, null, $"the site asks for {rules.CrawlDelay.Value.TotalSeconds:0} seconds between requests");

            using var response = await PacedGetAsync(uri, rules.CrawlDelay, "text/html,application/xhtml+xml,text/plain;q=0.9,application/pdf;q=0.8");
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                current = Resolve(uri, location).ToString();
                continue;
            }
            if (status is 401 or 403 or 407)
                return NotFetched(url, current, status, $"the site needs a sign-in or refused access ({status})");
            if (!response.IsSuccessStatusCode)
                return NotFetched(url, current, status, $"the site answered {status} {response.ReasonPhrase}".TrimEnd());
            return await ReadAsync(url, current, status, response);
        }
        return NotFetched(url, current, null, $"more than {MaxRedirects} redirects");
    }

    private async Task<FetchedPage> ReadAsync(string url, string finalUrl, int status, HttpResponseMessage response)
    {
        string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        bool html = type is "text/html" or "application/xhtml+xml";
        bool pdf = type == "application/pdf";
        if (!html && !pdf && type != "text/plain")
            return NotFetched(url, finalUrl, status, $"not a web page, text or PDF ({(type.Length > 0 ? type : "no type")})");
        if (response.Content.Headers.ContentLength > MaxBytes)
            return NotFetched(url, finalUrl, status, "larger than 10 MB");

        using var buffer = new MemoryStream();
        await using (var input = await response.Content.ReadAsStreamAsync())
        {
            if (!await DocumentRAGUtility.CopyWithLimitAsync(input, buffer, MaxBytes))
                return NotFetched(url, finalUrl, status, "larger than 10 MB");
        }
        byte[] bytes = buffer.ToArray();

        try
        {
            string title, text;
            if (pdf)
            {
                if (!Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Contains("%PDF-"))
                    return NotFetched(url, finalUrl, status, "said it was a PDF but is not one");
                (title, text) = PdfText(bytes);
            }
            else
            {
                string decoded = Decode(bytes, response.Content.Headers.ContentType);
                title = html ? PageText.TitleOf(decoded) : "";
                text = html ? PageText.FromHtml(decoded) : PageText.FromPlainText(decoded);
            }
            if (text.Length == 0)
                return NotFetched(url, finalUrl, status, "no readable text (the page may need JavaScript, or the PDF is scanned)");
            return new FetchedPage(url, finalUrl, status, type, title, text, null);
        }
        catch (RegexMatchTimeoutException)
        {
            return NotFetched(url, finalUrl, status, "the page could not be read in time");
        }
        catch (Exception ex) when (pdf && ex is not OutOfMemoryException)
        {
            // PdfPig throws its own exception types for damaged files.
            return NotFetched(url, finalUrl, status, "the PDF could not be read");
        }
    }

    private static (string Title, string Text) PdfText(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        string title = document.Information?.Title?.Trim() ?? "";
        var text = new StringBuilder();
        foreach (var page in document.GetPages()) text.Append(page.Text).Append('\n');
        return (title, PageText.FromPlainText(text.ToString()));
    }

    /// <summary>The charset the server names, else the one the page declares, else UTF-8.</summary>
    public static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        string? charset = contentType?.CharSet?.Trim('"', ' ');
        if (string.IsNullOrEmpty(charset))
        {
            var meta = Regex.Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)),
                @"<meta[^>]+charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (meta.Success) charset = meta.Groups[1].Value;
        }
        Encoding encoding;
        try { encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        string text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private async Task<RobotsRules> RobotsFor(Uri page)
    {
        string origin = page.GetLeftPart(UriPartial.Authority);
        if (_robots.TryGetValue(origin, out var cached) && DateTime.UtcNow - cached.Read < RobotsLifetime) return cached.Rules;

        RobotsRules rules;
        try
        {
            var robotsUri = new Uri(origin + "/robots.txt");
            rules = RobotsRules.DisallowAll;
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                using var response = await PacedGetAsync(robotsUri, null, "text/plain");
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is Uri location)
                {
                    robotsUri = Resolve(robotsUri, location);
                    if (!PrismaReviewEngine.IsWebAddress(robotsUri.ToString())) break;
                    continue;
                }
                if (response.IsSuccessStatusCode)
                {
                    using var buffer = new MemoryStream();
                    await using (var input = await response.Content.ReadAsStreamAsync())
                        await DocumentRAGUtility.CopyWithLimitAsync(input, buffer, RobotsRules.MaxLength);
                    rules = RobotsRules.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
                }
                else if (status is >= 400 and < 500) rules = RobotsRules.AllowAll; // no robots.txt (RFC 9309, 2.3.1.3)
                break; // 5xx: assume nothing may be fetched (2.3.1.4)
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            rules = RobotsRules.DisallowAll;
        }
        _robots[origin] = (rules, DateTime.UtcNow);
        return rules;
    }

    /// <summary>One GET, waiting first so the site gets at most one request per <see cref="MinInterval"/> (or its Crawl-delay).</summary>
    private async Task<HttpResponseMessage> PacedGetAsync(Uri uri, TimeSpan? crawlDelay, string accept)
    {
        string host = uri.Host;
        var gate = _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_nextRequest.TryGetValue(host, out var next) && next > DateTime.UtcNow)
                await _delay(next - DateTime.UtcNow);
            var interval = crawlDelay > MinInterval ? crawlDelay.Value : MinInterval;
            _nextRequest[host] = DateTime.UtcNow + interval;

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A redirect's target. A Location such as "/login" is relative, but on Linux and macOS .NET reads it as an
    /// absolute file path, so a file address is resolved against the page instead.
    /// </summary>
    public static Uri Resolve(Uri page, Uri location) =>
        location.IsAbsoluteUri && !location.IsFile ? location : new Uri(page, location.OriginalString);

    private static FetchedPage NotFetched(string url, string finalUrl, int? status, string why) =>
        new(url, finalUrl, status, "", "", "", why);

    private static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        AllowAutoRedirect = false, // followed by hand, so each hop is checked
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancel) =>
        {
            // Checked when connecting, not when reading the link, so a name that resolves to a private address is
            // refused too. The address checked is the address connected to.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
            var target = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} is not a public address");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Whether an address is on the public internet: not this machine, a private network, link-local or multicast.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // shared address space (carrier NAT)
                || (b[0] == 169 && b[1] == 254) // link-local, including cloud metadata services
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                || b[0] >= 224); // multicast and reserved
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC); // unique local fc00::/7
        }
        return false;
    }
}

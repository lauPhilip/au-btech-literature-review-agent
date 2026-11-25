using System.Net;
using System.Net.Http;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Full-text downloads are safe: each paper has its own file, named by a stable key; only public http(s) addresses are
/// fetched, redirects included; the size is capped; and a cancelled run stops the download and the reading.
/// </summary>
public class PdfDownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdfdl-").FullName;

    public void Dispose() => TestFolders.TryDelete(_root);

    private static readonly byte[] Pdf = TinyPdf.Write("text 12 72 700 We keep a running summary of the conversation in every agent loop.");

    /// <summary>A web server in a dictionary: address → status, body and redirect; it records every address asked for.</summary>
    private sealed class FakeWeb(Dictionary<string, (int Status, byte[] Body, string? Location)> pages) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, body, location) = pages.TryGetValue(url, out var page) ? page : (404, Array.Empty<byte>(), null);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new ByteArrayContent(body),
                Headers = { Location = location == null ? null : new Uri(location, UriKind.RelativeOrAbsolute) },
            });
        }
    }

    private static AcademicPaper Paper(string id, string title, string? doi = null) =>
        new(id, title, "", "2025", new() { "Ana Author" }, "Journal of Tests", doi);

    [Fact]
    public void TwoPapersWithTheSameTitleGetTwoFilesAndTheSamePaperAlwaysGetsTheSameOne()
    {
        const string title = "Context engineering for large language model agents: a systematic review of methods";
        var a = Paper("OPENALEX_W1", title, "10.1000/a");
        var b = Paper("OPENALEX_W2", title, "10.1000/b");

        Assert.Equal(DocumentRAGUtility.LegacyPdfFileName(a), DocumentRAGUtility.LegacyPdfFileName(b)); // the old names collided
        Assert.NotEqual(DocumentRAGUtility.PdfFileName(a), DocumentRAGUtility.PdfFileName(b));
        Assert.Equal(DocumentRAGUtility.PdfFileName(a), DocumentRAGUtility.PdfFileName(Paper("SCOPUS_9", "Another title", "https://doi.org/10.1000/A")));
        Assert.NotEqual(DocumentRAGUtility.PdfFileName(Paper("X1", title)), DocumentRAGUtility.PdfFileName(Paper("X2", title)));
        Assert.Matches("^fulltext-[0-9a-f]{20}\\.pdf$", DocumentRAGUtility.PdfFileName(a));
    }

    [Fact]
    public async Task EachPaperReadsItsOwnPdfAndAnOlderRunsCopyIsStillRead()
    {
        const string title = "Context engineering for large language model agents: a systematic review of methods";
        var a = Paper("OPENALEX_W1", title, "10.1000/a");
        var b = Paper("OPENALEX_W2", title, "10.1000/b");
        File.WriteAllBytes(Path.Join(_root, DocumentRAGUtility.PdfFileName(a)), TinyPdf.Write("text 12 72 700 Paper A keeps a running summary of every conversation it has."));
        File.WriteAllBytes(Path.Join(_root, DocumentRAGUtility.PdfFileName(b)), TinyPdf.Write("text 12 72 700 Paper B forgets its tools when the context window fills up."));

        var textA = await DocumentRAGUtility.IngestAndChunkPaperAsync(a, _root);
        var textB = await DocumentRAGUtility.IngestAndChunkPaperAsync(b, _root);

        Assert.StartsWith("Paper A", Assert.Single(textA.Chunks).Text);
        Assert.StartsWith("Paper B", Assert.Single(textB.Chunks).Text);

        var old = Paper("OPENALEX_W3", "An older run's paper with a file named by its title");
        File.WriteAllBytes(Path.Join(_root, DocumentRAGUtility.LegacyPdfFileName(old)), Pdf);
        var textOld = await DocumentRAGUtility.IngestAndChunkPaperAsync(old, _root);
        Assert.Equal("already downloaded", textOld.Source);
        Assert.Single(textOld.Chunks);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.org/paper.pdf")]
    [InlineData("http://localhost/paper.pdf")]
    [InlineData("http://127.0.0.1/paper.pdf")]
    [InlineData("http://10.0.0.5/paper.pdf")]
    [InlineData("http://192.168.1.1/paper.pdf")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://0.0.0.0/paper.pdf")]
    [InlineData("http://[::1]/paper.pdf")]
    [InlineData("http://[fd00::1]/paper.pdf")]
    [InlineData("https://user:secret@example.org/paper.pdf")]
    [InlineData("not an address")]
    public async Task AnAddressThatIsNotPublicHttpIsNeverAskedFor(string url)
    {
        var web = new FakeWeb(new() { [url] = (200, Pdf, null) });

        Assert.False(DocumentRAGUtility.IsDownloadable(url, out _));
        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync(url, Path.Join(_root, "p.pdf"), client: new HttpClient(web)));
        Assert.Empty(web.Requested);
    }

    [Fact]
    public void TheDownloadClientConnectsOnlyToPublicAddressesAfterTheNameIsResolved()
    {
        Assert.True(DocumentRAGUtility.IsDownloadable("https://arxiv.org/pdf/2401.00001.pdf", out _));
        // The handler checks the address a name resolves to (PageFetcher.IsPublic), so a public-looking name that
        // points inside the network is refused when connecting, not only when the link is read.
        Assert.False(PageFetcher.IsPublic(IPAddress.Parse("169.254.169.254")));
        Assert.False(PageFetcher.IsPublic(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.True(PageFetcher.IsPublic(IPAddress.Parse("93.184.216.34")));
    }

    [Fact]
    public async Task RedirectsAreFollowedByHandAndEveryHopIsChecked()
    {
        var web = new FakeWeb(new()
        {
            ["https://example.org/a"] = (302, Array.Empty<byte>(), "/b"),
            ["https://example.org/b"] = (301, Array.Empty<byte>(), "https://cdn.example.org/paper.pdf"),
            ["https://cdn.example.org/paper.pdf"] = (200, Pdf, null),
            ["https://example.org/metadata"] = (302, Array.Empty<byte>(), "http://169.254.169.254/latest/meta-data/"),
            ["https://example.org/local"] = (307, Array.Empty<byte>(), "file:///etc/passwd"),
        });
        var client = new HttpClient(web);

        Assert.True(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/a", Path.Join(_root, "ok.pdf"), client: client));
        Assert.True(File.Exists(Path.Join(_root, "ok.pdf")));
        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/metadata", Path.Join(_root, "m.pdf"), client: client));
        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/local", Path.Join(_root, "l.pdf"), client: client));
        Assert.DoesNotContain(web.Requested, u => u.Contains("169.254", StringComparison.Ordinal) || u.StartsWith("file:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AtMostFiveRedirectsAreFollowed()
    {
        var pages = new Dictionary<string, (int, byte[], string?)>();
        for (int i = 0; i < 7; i++) pages[$"https://example.org/{i}"] = (302, Array.Empty<byte>(), $"https://example.org/{i + 1}");
        pages["https://example.org/7"] = (200, Pdf, null);
        pages["https://example.org/x2"] = (302, Array.Empty<byte>(), "https://example.org/2");
        var web = new FakeWeb(pages);

        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/0", Path.Join(_root, "far.pdf"), client: new HttpClient(web)));
        Assert.Equal(DocumentRAGUtility.MaxRedirects + 1, web.Requested.Count);
        Assert.True(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/x2", Path.Join(_root, "near.pdf"), client: new HttpClient(web)));
    }

    [Fact]
    public async Task ATooLargeOrNonPdfDownloadIsDroppedAndLeavesNoPartialFile()
    {
        var web = new FakeWeb(new()
        {
            ["https://example.org/big.pdf"] = (200, Pdf.Concat(new byte[5000]).ToArray(), null),
            ["https://example.org/login"] = (200, "<html>Sign in</html>"u8.ToArray(), null),
        });
        var client = new HttpClient(web);

        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/big.pdf", Path.Join(_root, "big.pdf"), maxBytes: 4096, client: client));
        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/login", Path.Join(_root, "login.pdf"), client: client));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ACancelledRunStopsTheDownloadAndTheReading()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var web = new FakeWeb(new() { ["https://example.org/p.pdf"] = (200, Pdf, null) });
        string path = Path.Join(_root, "p.pdf");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentRAGUtility.TryDownloadPdfAsync("https://example.org/p.pdf", path, cancel: cancelled.Token, client: new HttpClient(web)));
        Assert.Empty(Directory.GetFiles(_root));

        var paper = Paper("OPENALEX_W9", "A paper with an open link") with { PdfUrl = "https://example.org/p.pdf" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentRAGUtility.IngestAndChunkPaperAsync(paper, _root, "", cancelled.Token));

        File.WriteAllBytes(path, Pdf);
        Assert.ThrowsAny<OperationCanceledException>(() => DocumentRAGUtility.ReadPdf(path, "p", cancelled.Token));
    }
}

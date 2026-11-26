using System.Net;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The open-access PDF download (#588): redirects followed by hand, at most five, each new address checked again;
/// the size cap kept; and a cancelled run stops the download instead of finishing it.
/// </summary>
public class PdfDownloadTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "pdf-download-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Join(_folder, "paper.pdf");

    public PdfDownloadTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TestFolders.TryDelete(_folder);

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% a tiny test file\n%%EOF");

    /// <summary>Answers each address from a script and remembers what was asked.</summary>
    private sealed class Web : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Answers = new();
        public readonly List<string> Asked = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string url = request.RequestUri!.ToString();
            Asked.Add(url);
            if (url.Contains("slow")) await Task.Delay(TimeSpan.FromSeconds(30), cancel);
            return Answers.TryGetValue(url, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        public static HttpResponseMessage Redirect(string to)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
            return response;
        }

        public static HttpResponseMessage File(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private static HttpClient Client(Web web) => new(web);

    [Fact]
    public async Task ARedirectToAPublicAddressIsFollowedAndRelativeOnesResolve()
    {
        var web = new Web();
        web.Answers["https://doi.example.org/10.1/x"] = () => Web.Redirect("https://repo.example.org/files/x");
        web.Answers["https://repo.example.org/files/x"] = () => Web.Redirect("/download/x.pdf");
        web.Answers["https://repo.example.org/download/x.pdf"] = () => Web.File(Pdf);

        Assert.True(await DocumentRAGUtility.TryDownloadPdfAsync("https://doi.example.org/10.1/x", Target, client: Client(web)));
        Assert.Equal(Pdf, File.ReadAllBytes(Target));
        Assert.Equal(3, web.Asked.Count);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:5000/api/workspace")]
    [InlineData("http://[::1]/x.pdf")]
    [InlineData("http://localhost/x.pdf")]
    [InlineData("https://user:password@example.org/x.pdf")]
    [InlineData("file:///etc/passwd")]
    public async Task ARedirectInsideTheNetworkIsNotFollowed(string inside)
    {
        var web = new Web();
        web.Answers["https://repo.example.org/x.pdf"] = () => Web.Redirect(inside);
        web.Answers[new Uri(inside).ToString()] = () => Web.File(Pdf);

        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://repo.example.org/x.pdf", Target, client: Client(web)));
        Assert.Equal(new[] { "https://repo.example.org/x.pdf" }, web.Asked);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task ALinkThatPointsInsideTheNetworkIsNeverAsked()
    {
        var web = new Web();
        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("http://10.0.0.8/x.pdf", Target, client: Client(web)));
        Assert.Empty(web.Asked);
    }

    [Fact]
    public async Task AtMostFiveRedirectsAreFollowed()
    {
        var web = new Web();
        for (int i = 0; i < 6; i++)
        {
            int next = i + 1;
            web.Answers[$"https://repo.example.org/{i}"] = () => Web.Redirect($"https://repo.example.org/{next}");
        }
        web.Answers["https://repo.example.org/6"] = () => Web.File(Pdf);

        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://repo.example.org/0", Target, client: Client(web)));
        Assert.Equal(DocumentRAGUtility.MaxPdfRedirects + 1, web.Asked.Count);

        web.Asked.Clear();
        Assert.True(await DocumentRAGUtility.TryDownloadPdfAsync("https://repo.example.org/1", Target, client: Client(web)));
    }

    [Fact]
    public async Task AFileOverTheCapOrNotAPdfIsNotKept()
    {
        var web = new Web();
        web.Answers["https://repo.example.org/big.pdf"] = () => Web.File(Pdf.Concat(new byte[4096]).ToArray());
        web.Answers["https://repo.example.org/login.pdf"] = () => Web.File(Encoding.ASCII.GetBytes("<html>Sign in</html>"));

        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://repo.example.org/big.pdf", Target, maxBytes: 1024, client: Client(web)));
        Assert.False(await DocumentRAGUtility.TryDownloadPdfAsync("https://repo.example.org/login.pdf", Target, client: Client(web)));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task ACancelledRunStopsTheDownload()
    {
        var web = new Web();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentRAGUtility.TryDownloadPdfAsync("https://repo.example.org/slow.pdf", Target, cancel: stop.Token, client: Client(web)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(_folder));
    }
}

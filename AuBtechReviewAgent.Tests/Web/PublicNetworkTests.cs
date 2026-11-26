using System.Net;
using System.Net.Sockets;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The connection guard every download from an outside address goes through (#588): only http(s), no user name or
/// password, and only public addresses, checked again when the name is resolved.
/// </summary>
public class PublicNetworkTests
{
    [Theory]
    [InlineData("https://arxiv.org/pdf/2401.00001.pdf", null)]
    [InlineData("http://example.org/paper.pdf", null)]
    [InlineData("ftp://example.org/paper.pdf", "not an http or https address")]
    [InlineData("file:///etc/passwd", "not an http or https address")]
    [InlineData("https://user:secret@example.org/paper.pdf", "the address carries a user name or password")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "the address is not on the public internet")]
    [InlineData("http://127.0.0.1:8080/admin", "the address is not on the public internet")]
    [InlineData("http://10.0.0.5/paper.pdf", "the address is not on the public internet")]
    [InlineData("http://[::1]/paper.pdf", "the address is not on the public internet")]
    [InlineData("http://[::ffff:192.168.0.1]/paper.pdf", "the address is not on the public internet")]
    [InlineData("http://localhost/paper.pdf", "the address is not on the public internet")]
    [InlineData("http://api.localhost/paper.pdf", "the address is not on the public internet")]
    public void AnOutsideAddressIsCheckedBeforeItIsFetched(string url, string? why) =>
        Assert.Equal(why, PublicNetwork.WhyNotAllowed(new Uri(url)));

    [Fact]
    public void ARelativeOrMissingAddressIsNotFetched()
    {
        Assert.Equal("not a web address", PublicNetwork.WhyNotAllowed(null));
        Assert.Equal("not a web address", PublicNetwork.WhyNotAllowed(new Uri("/paper.pdf", UriKind.Relative)));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("fd00::1", false)]
    [InlineData("8.8.8.8", true)]
    public void ThePageFetcherUsesTheSameGuard(string address, bool isPublic)
    {
        Assert.Equal(isPublic, PublicNetwork.IsPublic(IPAddress.Parse(address)));
        Assert.Equal(isPublic, PageFetcher.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task TheHandlerNeverConnectsToThisMachine()
    {
        // A server on loopback that would answer if it were reached; the guard refuses before connecting.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(PublicNetwork.Handler()) { Timeout = TimeSpan.FromSeconds(10) };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://localhost:{port}/paper.pdf"));

        Assert.Contains("not a public address", error.ToString());
        Assert.False(listener.Pending(), "nothing connected to the server");
    }
}

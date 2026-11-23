using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The site's own rate limits (SiteRateLimits) through the real app: every plain HTTP address counts per client
/// address, a burst beyond the limit gets 429 with Retry-After, and the server itself is not limited. The test server
/// gives requests no address, so a start-up filter sets one from a test header.
/// </summary>
[Collection(ApiCollection.Name)]
public class SiteRateLimitTests
{
    private const string AddressHeader = "X-Test-Address";
    private readonly ITestOutputHelper _output;

    public SiteRateLimitTests(ITestOutputHelper output) => _output = output;

    private sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MISTRAL_API_KEY", ApiTestHost.ServerKey);
            builder.UseSetting("Site:Limits:ProbesPerMinute", "30");
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, AddressFromHeader>());
        }
    }

    private sealed class AddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[AddressHeader].ToString(), out var address)) context.Connection.RemoteIpAddress = address;
                await inner(context);
            });
            next(app);
        };
    }

    private static HttpRequestMessage Get(string path, string? address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (address != null) request.Headers.Add(AddressHeader, address);
        return request;
    }

    [Theory]
    [InlineData("/", SiteRateLimits.Pages)]
    [InlineData("/review/5d0c6c0e-1111-4c4c-9a9a-123456789abc", SiteRateLimits.Pages)]
    [InlineData("/verify", SiteRateLimits.Pages)]
    [InlineData("/_blazor", SiteRateLimits.Connections)]
    [InlineData("/_blazor/negotiate", SiteRateLimits.Negotiations)]
    [InlineData("/_framework/blazor.web.js", SiteRateLimits.StaticFiles)]
    [InlineData("/css/tailwind.abc123.css", SiteRateLimits.StaticFiles)]
    [InlineData("/health", SiteRateLimits.Probes)]
    [InlineData("/sitemap.xml", SiteRateLimits.Probes)]
    [InlineData("/api/workspace/5d0c6c0e-1111-4c4c-9a9a-123456789abc/main.tex", SiteRateLimits.Downloads)]
    [InlineData("/metrics", SiteRateLimits.Pages)]
    [InlineData("/metrics/runs.csv", SiteRateLimits.Downloads)]
    [InlineData("/api/v1/reviews", SiteRateLimits.Api)]
    [InlineData("/mcp", SiteRateLimits.Api)]
    public void EveryAddressCountsAgainstOneLimit(string path, string category) =>
        Assert.Equal(category, SiteRateLimits.Category(new PathString(path)));

    [Fact]
    public async Task A_burst_from_one_address_gets_429_with_Retry_After_and_other_addresses_and_the_server_do_not()
    {
        using var host = new Host();
        var client = host.CreateClient();
        var clock = Stopwatch.StartNew();
        var answers = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => client.SendAsync(Get("/health", "203.0.113.50"))));
        clock.Stop();
        int ok = answers.Count(a => a.StatusCode != HttpStatusCode.TooManyRequests);
        var refused = answers.Where(a => a.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        _output.WriteLine($"100 requests to /health from one address in {clock.ElapsedMilliseconds} ms: {ok} answered, {refused.Count} refused with 429");

        Assert.Equal(30, ok);
        Assert.Equal(70, refused.Count);
        Assert.All(refused, r => Assert.True(r.Headers.RetryAfter?.Delta > TimeSpan.Zero));
        Assert.Contains("Too many requests", await refused[0].Content.ReadAsStringAsync());

        // Another address, and the server itself (loopback, or no address at all), are not held up by that burst.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/health", "198.51.100.9"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/health", "127.0.0.1"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/health", null))).StatusCode);
        // An IPv6 visitor cannot escape the count by changing the last half of the address.
        var v6 = await Task.WhenAll(Enumerable.Range(0, 31).Select(i => client.SendAsync(Get("/health", $"2001:db8:1:2::{i + 1:x}"))));
        Assert.Equal(1, v6.Count(a => a.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task A_body_larger_than_the_limit_is_refused()
    {
        using var host = new Host();
        var client = host.CreateClient();
        using var content = new ByteArrayContent(new byte[2 * 1024 * 1024]);
        var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = content };
        request.Headers.Add(AddressHeader, "203.0.113.60");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode);
    }
}

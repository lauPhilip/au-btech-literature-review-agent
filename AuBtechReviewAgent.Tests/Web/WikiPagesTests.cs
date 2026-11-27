using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The developer wiki served by the whole app under /docs (issue #613): indexable, linked, and safe.</summary>
[Collection(ApiCollection.Name)]
public sealed class WikiPagesTests
{
    private sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("MISTRAL_API_KEY", "server-key-for-the-wiki-test");
            builder.UseSetting("Site:PublicUrl", "https://reviews.example.org/");
        }
    }

    [Fact]
    public async Task EveryWikiPageIsServedWithItsTitleCanonicalAddressAndSource()
    {
        using var host = new Host();
        using var client = host.CreateClient();

        foreach (var page in WikiDocs.Pages)
        {
            string path = WikiDocs.PathOf(page);
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string html = await response.Content.ReadAsStringAsync();

            Assert.Contains("<link rel=\"canonical\" href=\"https://reviews.example.org" + path + "\"", html);
            Assert.DoesNotContain("<meta name=\"robots\" content=\"noindex", html);
            Assert.False(response.Headers.Contains("X-Robots-Tag"));
            Assert.Contains("class=\"ui-wiki", html);
            Assert.Contains("href=\"" + WikiDocs.SourceUrl(page) + "\"", html);
            Assert.Contains("aria-current=\"page\"", html);
            // Every other wiki page is one click away.
            Assert.All(WikiDocs.Pages, other => Assert.Contains("href=\"" + WikiDocs.PathOf(other) + "\"", html));
        }
    }

    [Theory]
    [InlineData("/docs/no-such-page")]
    [InlineData("/docs/README")]
    [InlineData("/docs/api")] // the API page's own address: with the API off it is not found, and it is not a wiki page
    public async Task AnUnknownPageIsNotFound(string path)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task TheSitemapAndLlmsTxtListTheWiki()
    {
        using var host = new Host();
        using var client = host.CreateClient();

        string sitemap = await client.GetStringAsync("/sitemap.xml");
        Assert.Contains("<loc>https://reviews.example.org/docs</loc>", sitemap);
        Assert.Contains("<loc>https://reviews.example.org/docs/02-review-pipeline</loc>", sitemap);
        Assert.Contains("(https://reviews.example.org/docs)", await client.GetStringAsync("/llms.txt"));
    }
}

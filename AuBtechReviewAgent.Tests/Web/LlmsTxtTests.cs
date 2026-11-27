using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>/llms.txt (issue #610): served at the root as text, says what the tool is, and every address on the site resolves.</summary>
[Collection(ApiCollection.Name)]
public partial class LlmsTxtTests
{
    [GeneratedRegex(@"\]\((https?://[^)\s]+)\)")]
    private static partial Regex Links();

    [Fact]
    public async Task It_is_served_at_the_root_as_plain_text_with_the_name_the_repository_and_the_DOI()
    {
        using var host = new ApiTestHost();
        var response = await host.CreateClient().GetAsync("/llms.txt");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("# TraceableAI", text);
        Assert.Contains(SiteSeo.DefaultDescription, text);
        Assert.Contains(SiteSeo.RepositoryUrl, text);
        Assert.Contains("https://doi.org/10.5281/zenodo.24379094", text);
    }

    [Fact]
    public async Task With_the_API_on_the_pages_come_in_order_and_every_address_on_the_site_resolves()
    {
        using var host = new ApiTestHost();
        var client = host.CreateClient();
        string text = await client.GetStringAsync("/llms.txt");
        var links = Links().Matches(text).Select(m => new Uri(m.Groups[1].Value)).ToList();

        string[] ownPaths = links.Where(u => u.Host == client.BaseAddress!.Host).Select(u => u.AbsolutePath).ToArray();
        Assert.Equal(new[] { "/", "/start", "/glossary", "/about", "/docs/api", "/openapi/v1.json", "/mcp" }, ownPaths);
        foreach (string path in ownPaths)
        {
            var status = (await client.GetAsync(path)).StatusCode;
            Assert.True(status != HttpStatusCode.NotFound, $"{path} answered 404");
        }
        Assert.Equal(new[] { "github.com", "doi.org" }, links.Where(u => u.Host != client.BaseAddress!.Host).Select(u => u.Host));
    }

    [Fact]
    public async Task With_the_API_off_the_addresses_that_would_answer_404_are_left_out()
    {
        using var host = new ApiTestHost(enabled: false);
        var client = host.CreateClient();
        string text = await client.GetStringAsync("/llms.txt");

        Assert.DoesNotContain("/docs/api", text);
        Assert.DoesNotContain("/openapi", text);
        Assert.DoesNotContain("/mcp", text);
        foreach (var link in Links().Matches(text).Select(m => new Uri(m.Groups[1].Value)).Where(u => u.Host == client.BaseAddress!.Host))
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(link.AbsolutePath)).StatusCode);
    }
}

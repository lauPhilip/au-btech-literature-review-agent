using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The ScholarlyArticle block and the DOI citation on a gallery version's page, served by the whole app.</summary>
[Collection(ApiCollection.Name)]
public sealed class GalleryStructuredDataPagesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-ld-pages-" + Guid.NewGuid().ToString("N"));

    private sealed class Host(string folder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("MISTRAL_API_KEY", "server-key-for-the-gallery-test");
            builder.UseSetting("Gallery:Folder", folder);
            builder.UseSetting("Site:PublicUrl", "https://reviews.example.org/");
        }
    }

    public void Dispose() => TestFolders.TryDelete(_root);

    private async Task<GalleryEntry> AddedAsync(string? doi)
    {
        string folder = Path.Join(_root, "gallery");
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var gallery = new GalleryStore(new GalleryOptions { Folder = folder }, _root, runs);
        var (id, key) = await GalleryStoreTests.FinishedRunAsync(runs, "AI in schools", new[] { "What is known?" }, "AI & tutors in schools");
        var entry = (await gallery.AddAsync(id, key, consent: true)).Entry!;
        if (doi != null)
        {
            // The maintainer records a DOI by editing the version in entry.json.
            string path = Path.Join(folder, entry.Id, GalleryEntry.FileName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["Versions"]![0]!["Doi"] = doi;
            json["Doi"] = doi;
            File.WriteAllText(path, json.ToJsonString());
        }
        return entry;
    }

    private static JsonElement LdBlock(string html, string type)
    {
        foreach (Match m in Regex.Matches(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline))
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            if (doc.RootElement.GetProperty("@type").GetString() == type) return doc.RootElement.Clone();
        }
        throw new Xunit.Sdk.XunitException($"No {type} block in the page.");
    }

    [Fact]
    public async Task EveryVersionPageCarriesScholarlyArticleDataAndACitationToItsDoi()
    {
        var entry = await AddedAsync("10.5281/zenodo.1234567");
        using var host = new Host(Path.Join(_root, "gallery"));
        using var client = host.CreateClient();

        foreach (string path in new[] { $"/gallery/{entry.Id}", $"/gallery/{entry.Id}/v/1" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string html = await response.Content.ReadAsStringAsync();

            var article = LdBlock(html, "ScholarlyArticle");
            Assert.Equal("AI & tutors in schools", article.GetProperty("headline").GetString());
            Assert.Equal("10.5281/zenodo.1234567", article.GetProperty("identifier").GetProperty("value").GetString());
            Assert.Equal($"https://reviews.example.org/gallery/{entry.Id}", article.GetProperty("isPartOf").GetProperty("url").GetString());
            Assert.Contains("https://doi.org/10.5281/zenodo.1234567", html);
        }
    }

    [Fact]
    public async Task WithoutADoiThePageStillHasTheDataAndCitesTheSiteAddress()
    {
        var entry = await AddedAsync(doi: null);
        using var host = new Host(Path.Join(_root, "gallery"));
        using var client = host.CreateClient();

        string html = await client.GetStringAsync($"/gallery/{entry.Id}/v/1");

        var article = LdBlock(html, "ScholarlyArticle");
        Assert.False(article.TryGetProperty("identifier", out _));
        Assert.DoesNotContain("https://doi.org/", html);
        Assert.Contains($"https://reviews.example.org/gallery/{entry.Id}/v/1", html);
    }
}

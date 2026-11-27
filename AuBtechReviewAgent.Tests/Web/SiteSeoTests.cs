using System.Text.Json;
using System.Xml.Linq;
using AuBtechReviewAgent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>What search engines get: robots.txt, the sitemap, the noindex rules and the structured data.</summary>
public class SiteSeoTests
{
    private const string Base = "https://traceable.example.org";

    [Fact]
    public void SitemapListsOnlyThePublicPagesAsAbsoluteUrls()
    {
        var doc = XDocument.Parse(SiteSeo.SitemapXml(Base));
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locs = doc.Descendants(ns + "loc").Select(e => e.Value).ToList();

        Assert.Equal(new[] { "/", "/start", "/review", "/metrics", "/glossary", "/about", "/model-card", "/privacy", "/verify", "/accessibility" }.Select(p => Base + p), locs);
        Assert.Equal("urlset", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void TheWikiPagesFollowThePublicPagesInTheSitemap()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var doc = XDocument.Parse(SiteSeo.SitemapXml(Base, new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc), WikiDocs.SitemapPages));
        var locs = doc.Descendants(ns + "loc").Select(e => e.Value).ToList();

        Assert.Equal(SiteSeo.IndexedPages.Count + WikiDocs.Pages.Count, locs.Count);
        Assert.Equal(Base + "/docs", locs[SiteSeo.IndexedPages.Count]);
        Assert.Contains(Base + "/docs/01-system-overview", locs);
        Assert.All(doc.Descendants(ns + "url"), url => Assert.Equal("2026-11-27", url.Element(ns + "lastmod")?.Value));
    }

    [Fact]
    public void EveryPageInTheSitemapHasTheDayTheVersionWasBuilt()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var dated = XDocument.Parse(SiteSeo.SitemapXml(Base, new DateTime(2026, 11, 22, 23, 30, 0, DateTimeKind.Utc)));
        var undated = XDocument.Parse(SiteSeo.SitemapXml(Base));

        Assert.All(dated.Descendants(ns + "url"), url => Assert.Equal("2026-11-22", url.Element(ns + "lastmod")?.Value));
        Assert.Equal(10, dated.Descendants(ns + "lastmod").Count());
        Assert.Empty(undated.Descendants(ns + "lastmod"));
    }

    [Fact]
    public void ThePreviewCardIsASelfHosted1200By630Png()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository (packaged tests)

        Assert.False(SiteSeo.SocialImagePath.Contains("://", StringComparison.Ordinal));
        byte[] png = File.ReadAllBytes(Path.Join(root, "AuBtechReviewAgent", "wwwroot", SiteSeo.SocialImagePath));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        int Int(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
        Assert.Equal((1200, 630), (Int(16), Int(20))); // the IHDR chunk's width and height
    }

    [Fact]
    public void RobotsPointsToTheSitemapAndKeepsCrawlersOutOfDownloads()
    {
        string robots = SiteSeo.RobotsTxt(Base);

        Assert.Contains("Sitemap: " + Base + "/sitemap.xml", robots);
        Assert.Contains("Disallow: /api/", robots);
        // Run pages are not blocked: a crawler has to fetch them to see their noindex.
        Assert.DoesNotContain("Disallow: /review", robots);
    }

    [Theory]
    [InlineData("/api/workspace/0b6b1f0e-0000-0000-0000-000000000000/archive", true)]
    [InlineData("/review/0b6b1f0e-0000-0000-0000-000000000000", true)]
    [InlineData("/spec-matrix", true)]
    [InlineData("/spec-matrix/0b6b1f0e-0000-0000-0000-000000000000", true)]
    [InlineData("/Error", true)]
    [InlineData("/design/report", true)]
    [InlineData("/", false)]
    [InlineData("/review", false)]
    [InlineData("/metrics", false)]
    [InlineData("/sitemap.xml", false)]
    [InlineData("/glossary", false)]
    public void OnlyRunPagesDownloadsAndErrorsAreNoIndex(string path, bool noIndex)
    {
        Assert.Equal(noIndex, SiteSeo.IsNoIndexPath(path));
    }

    [Fact]
    public void ConfiguredPublicUrlWinsOverTheRequestHost()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Site:PublicUrl"] = Base + "/" }).Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("internal-host:8080");

        Assert.Equal(Base, SiteSeo.BaseUrl(config, context.Request));
        Assert.Equal("http://internal-host:8080", SiteSeo.BaseUrl(new ConfigurationBuilder().Build(), context.Request));
    }

    [Fact]
    public void StructuredDataIsValidJsonThatCannotCloseItsScriptElement()
    {
        string json = SiteSeo.JsonLd(Base);

        Assert.DoesNotContain("<", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SoftwareApplication", doc.RootElement.GetProperty("@type").GetString());
        Assert.Equal(Base + "/", doc.RootElement.GetProperty("url").GetString());
    }
}

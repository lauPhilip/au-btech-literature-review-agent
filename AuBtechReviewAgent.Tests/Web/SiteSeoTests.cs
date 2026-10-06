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

        Assert.Equal(new[] { "/", "/start", "/review", "/metrics", "/glossary", "/about", "/model-card", "/privacy", "/verify" }.Select(p => Base + p), locs);
        Assert.Equal("urlset", doc.Root!.Name.LocalName);
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

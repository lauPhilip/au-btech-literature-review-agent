using System;
using System.Collections.Generic;
using System.Linq;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The AI-crawler policy in robots.txt (issue #611): named on purpose, /api/ closed, the sitemap last.</summary>
public class RobotsPolicyTests
{
    private const string Base = "https://traceable.example.org";

    /// <summary>Reads the file into groups: the user agents that share a block of rules, and the rules.</summary>
    private static List<(List<string> Agents, List<string> Rules)> Groups(string robots)
    {
        var groups = new List<(List<string>, List<string>)>();
        List<string>? agents = null, rules = null;
        foreach (string raw in robots.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("Sitemap:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("User-agent:", StringComparison.Ordinal))
            {
                if (rules is { Count: > 0 }) { agents = null; rules = null; }
                if (agents == null) { agents = new(); rules = new(); groups.Add((agents, rules)); }
                agents.Add(line["User-agent:".Length..].Trim());
            }
            else rules?.Add(line);
        }
        return groups;
    }

    [Fact]
    public void EveryNamedAiCrawlerMayFetchThePublicPagesButNotTheApi()
    {
        var groups = Groups(SiteSeo.RobotsTxt(Base));

        foreach (string crawler in SiteSeo.AiCrawlers)
        {
            var group = Assert.Single(groups, g => g.Agents.Contains(crawler));
            Assert.Equal(new[] { "Allow: /", "Disallow: /api/" }, group.Rules);
        }
        foreach (string expected in new[] { "GPTBot", "ClaudeBot", "PerplexityBot", "Google-Extended", "CCBot", "anthropic-ai", "OAI-SearchBot" })
            Assert.Contains(expected, SiteSeo.AiCrawlers);
    }

    [Fact]
    public void EveryGroupClosesTheApiOnce()
    {
        foreach (var group in Groups(SiteSeo.RobotsTxt(Base)))
            Assert.Single(group.Rules, r => r == "Disallow: /api/");
    }

    [Fact]
    public void TheGroupForAllCrawlersIsUnchanged()
    {
        var all = Assert.Single(Groups(SiteSeo.RobotsTxt(Base)), g => g.Agents.Contains("*"));
        Assert.Equal(new[] { "*" }, all.Agents);
        Assert.Equal(new[] { "Allow: /", "Disallow: /api/" }, all.Rules);
    }

    [Fact]
    public void TheSitemapLineIsLastAndRunPagesAreNotBlocked()
    {
        string robots = SiteSeo.RobotsTxt(Base);

        Assert.Equal("Sitemap: " + Base + "/sitemap.xml", robots.TrimEnd().Split('\n').Last());
        Assert.Single(robots.Split('\n'), l => l.StartsWith("Sitemap:", StringComparison.Ordinal));
        Assert.DoesNotContain("Disallow: /review", robots);
        Assert.Contains("noindex", robots);
    }
}

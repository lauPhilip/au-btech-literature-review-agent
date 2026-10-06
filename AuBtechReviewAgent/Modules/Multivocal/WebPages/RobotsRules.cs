using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The rules of one site's robots.txt for this app, read as RFC 9309 says: the group naming our product token
/// ("TraceableAI") if there is one, otherwise the "*" group; the longest matching rule wins, and on a tie "allow"
/// wins; "*" matches any characters and "$" anchors the end. Crawl-delay is not in the RFC but is honoured, since
/// sites that set it ask for exactly the politeness the review owes them.
/// </summary>
public sealed class RobotsRules
{
    /// <summary>The product token our user agent sends (see <see cref="OpenSourceHttp.UserAgent"/>).</summary>
    public const string ProductToken = "TraceableAI";

    /// <summary>At most this much of a robots.txt is read; RFC 9309 asks crawlers to read at least 500 KiB.</summary>
    public const int MaxLength = 512 * 1024;

    private readonly List<(string Pattern, bool Allow)> _rules;

    private RobotsRules(List<(string, bool)> rules, TimeSpan? crawlDelay, string why)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
        Why = why;
    }

    /// <summary>The site's Crawl-delay for us, when it sets one.</summary>
    public TimeSpan? CrawlDelay { get; }

    /// <summary>Where these rules come from, for the reason a page was not fetched.</summary>
    public string Why { get; }

    /// <summary>No robots.txt (4xx): everything may be fetched.</summary>
    public static RobotsRules AllowAll { get; } = new(new(), null, "no robots.txt");

    /// <summary>robots.txt could not be read (5xx or no answer): RFC 9309 says to assume nothing may be fetched.</summary>
    public static RobotsRules DisallowAll { get; } = new(new() { ("/", false) }, null, "robots.txt could not be read");

    public static RobotsRules Parse(string text)
    {
        if (text.Length > MaxLength) text = text[..MaxLength];
        var groups = new List<Group>();
        Group? current = null;
        bool lastWasAgent = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            if (key == "user-agent")
            {
                if (current == null || !lastWasAgent)
                {
                    current = new Group();
                    groups.Add(current);
                }
                current.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (current == null) continue; // rules before any user-agent line belong to no group

            if (key is "allow" or "disallow")
            {
                if (value.Length == 0) continue; // "Disallow:" with nothing means no rule
                current.Rules.Add((value, key == "allow"));
            }
            else if (key == "crawl-delay" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0)
                current.Delay = TimeSpan.FromSeconds(Math.Min(seconds, 86400));
        }

        string token = ProductToken.ToLowerInvariant();
        var ours = groups.Where(g => g.Agents.Any(a => a == token)).ToList();
        string why = "the site's robots.txt for TraceableAI";
        if (ours.Count == 0)
        {
            ours = groups.Where(g => g.Agents.Contains("*")).ToList();
            why = "the site's robots.txt";
        }
        // Several groups for the same agent are combined (RFC 9309, 2.2.1).
        var delay = ours.Select(g => g.Delay).Where(d => d != null).DefaultIfEmpty(null).Max();
        return new RobotsRules(ours.SelectMany(g => g.Rules).ToList(), delay, why);
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = new();
        public List<(string Pattern, bool Allow)> Rules { get; } = new();
        public TimeSpan? Delay { get; set; }
    }

    /// <summary>Whether a path (with its query) may be fetched. /robots.txt itself always may.</summary>
    public bool Allows(string pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
        if (pathAndQuery.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase)) return true;
        int best = -1;
        bool allow = true;
        foreach (var (pattern, isAllow) in _rules)
        {
            if (!Matches(pattern, pathAndQuery)) continue;
            if (pattern.Length > best || (pattern.Length == best && isAllow))
            {
                best = pattern.Length;
                allow = isAllow;
            }
        }
        return allow;
    }

    /// <summary>Matches a robots.txt pattern from the start of the path: "*" is any run of characters, a final "$" the end.</summary>
    public static bool Matches(string pattern, string path)
    {
        bool anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];
        string[] parts = Unescape(pattern).Split('*');
        path = Unescape(path);
        if (!path.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        int at = parts[0].Length;
        for (int i = 1; i < parts.Length; i++)
        {
            if (i == parts.Length - 1 && anchored)
                return path.Length - at >= parts[i].Length && path.EndsWith(parts[i], StringComparison.Ordinal);
            int found = path.IndexOf(parts[i], at, StringComparison.Ordinal);
            if (found < 0) return false;
            at = found + parts[i].Length;
        }
        return !anchored || at == path.Length;
    }

    // Paths are compared with percent-escapes decoded, so "/a%20b" and "/a b" are the same path (RFC 9309, 2.2.2).
    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }
}

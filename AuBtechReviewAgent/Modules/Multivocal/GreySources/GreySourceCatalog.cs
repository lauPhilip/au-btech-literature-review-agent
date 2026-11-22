using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The grey literature searches that are built, by their key in the plan (<see cref="MultivocalGuidelines.GreySearches"/>),
/// and the kinds of grey literature each can find. Backlink snowballing follows once sources are included (block E);
/// until then <see cref="Create"/> returns null for it and it finds nothing.
/// </summary>
public static class GreySourceCatalog
{
    private static readonly Dictionary<string, Func<string, IGreySource>> Factories = new(StringComparer.Ordinal)
    {
        ["stackexchange"] = _ => new StackExchangeSource(),
        ["github"] = _ => new GitHubSource(),
        ["hackernews"] = _ => new HackerNewsSource(),
        ["openalex-grey"] = email => new OpenAlexGreySource(email),
        ["zenodo"] = _ => new ZenodoSource(),
    };

    // The kinds each search gives its sources (the Kind each source's Parse sets), so the plan can say what it can find (#249).
    private static readonly Dictionary<string, string[]> Kinds = new(StringComparer.Ordinal)
    {
        ["stackexchange"] = new[] { "qa" },
        ["github"] = new[] { "code" },
        ["hackernews"] = new[] { "blogs", "qa" },
        ["openalex-grey"] = new[] { "white-papers", "theses", "documentation" },
        ["zenodo"] = new[] { "white-papers", "theses", "documentation" },
    };

    public static bool IsBuilt(string key) => Factories.ContainsKey(key);

    /// <summary>
    /// A new source for this key, or null when the search is not built yet. <paramref name="contactEmail"/> is the
    /// OpenSources contact e-mail, which OpenAlex asks for.
    /// </summary>
    public static IGreySource? Create(string key, string contactEmail = "") =>
        Factories.TryGetValue(key, out var create) ? create(contactEmail) : null;

    /// <summary>The kinds of grey literature a search can find; none for a search that is not built.</summary>
    public static IReadOnlyList<string> KindsFoundBy(string search) =>
        IsBuilt(search) && Kinds.TryGetValue(search, out var kinds) ? kinds : Array.Empty<string>();

    /// <summary>For each kind the plan names, the chosen searches that can find it, in the order of the form; empty when none can.</summary>
    public static IReadOnlyList<(string Kind, IReadOnlyList<string> Searches)> Reach(IEnumerable<string> kinds, IEnumerable<string> searches)
    {
        var chosen = searches.ToList();
        return kinds.Select(kind => (kind, (IReadOnlyList<string>)MultivocalGuidelines.GreySearches
            .Where(s => chosen.Contains(s.Key) && KindsFoundBy(s.Key).Contains(kind)).Select(s => s.Key).ToList())).ToList();
    }
}

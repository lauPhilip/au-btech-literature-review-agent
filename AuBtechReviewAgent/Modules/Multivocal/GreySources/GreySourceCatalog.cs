using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The grey literature searches that are built, by their key in the plan (<see cref="MultivocalGuidelines.GreySearches"/>).
/// The others (grey literature in OpenAlex, backlink snowballing, Brave with the reviewer's own key) follow in
/// MLR block D; until then <see cref="Create"/> returns null for them.
/// </summary>
public static class GreySourceCatalog
{
    private static readonly Dictionary<string, Func<IGreySource>> Factories = new(StringComparer.Ordinal)
    {
        ["stackexchange"] = () => new StackExchangeSource(),
        ["github"] = () => new GitHubSource(),
        ["hackernews"] = () => new HackerNewsSource(),
        ["zenodo"] = () => new ZenodoSource(),
    };

    public static bool IsBuilt(string key) => Factories.ContainsKey(key);

    /// <summary>A new source for this key, or null when the search is not built yet.</summary>
    public static IGreySource? Create(string key) => Factories.TryGetValue(key, out var create) ? create() : null;
}

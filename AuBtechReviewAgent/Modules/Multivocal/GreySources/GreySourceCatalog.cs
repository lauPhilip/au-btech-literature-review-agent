using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The grey literature searches that are built, by their key in the plan (<see cref="MultivocalGuidelines.GreySearches"/>).
/// Backlink snowballing follows once sources are included (block E); until then <see cref="Create"/> returns null for it.
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

    public static bool IsBuilt(string key) => Factories.ContainsKey(key);

    /// <summary>
    /// A new source for this key, or null when the search is not built yet. <paramref name="contactEmail"/> is the
    /// OpenSources contact e-mail, which OpenAlex asks for.
    /// </summary>
    public static IGreySource? Create(string key, string contactEmail = "") =>
        Factories.TryGetValue(key, out var create) ? create(contactEmail) : null;
}

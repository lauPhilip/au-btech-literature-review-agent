using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>Which API key a source gateway needs before it can be queried.</summary>
public enum SourceKeyKind { None, Elsevier, Ieee, SerpApi }

public record SourceOption(string Key, string DisplayName, SourceKeyKind KeyKind, string KeyLabel);

/// <summary>
/// The fixed list of source gateways a user can tick in the dashboard. The Key is what gets stored in
/// the run ledger (ReviewState.SelectedSources); DisplayName is what the report prints.
/// </summary>
public static class SourceCatalog
{
    public static readonly IReadOnlyList<SourceOption> All = new List<SourceOption>
    {
        new("arxiv",        "arXiv",                     SourceKeyKind.None,     ""),
        new("scopus",       "Scopus / ScienceDirect",    SourceKeyKind.Elsevier, "Elsevier key"),
        new("ieee",         "IEEE Xplore",               SourceKeyKind.Ieee,     "IEEE key"),
        new("scholar",      "Google Scholar",            SourceKeyKind.SerpApi,  "SerpApi key"),
        new("researchgate", "ResearchGate (via SerpApi)", SourceKeyKind.SerpApi, "SerpApi key"),
    };

    public static IReadOnlyList<string> AllKeys => All.Select(s => s.Key).ToList();

    public static SourceOption? Find(string key) =>
        All.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string DisplayNameFor(string key) => Find(key)?.DisplayName ?? key;

    /// <summary>
    /// appsettings.json ships with "YOUR_..._PLACEHOLDER" values. Those used to count as real keys, so
    /// the engine queried Scopus/IEEE/SerpApi with a placeholder and logged a faulted search every run.
    /// </summary>
    public static bool IsUsableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && !key.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
        && !key.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
}

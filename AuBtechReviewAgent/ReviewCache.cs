using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Cache" section of appsettings.json.</summary>
public class CacheOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a source's search response is reused. Kept short on purpose: a review should reflect the
    /// databases on the day it is run. The response actually used is always saved with the run.
    /// </summary>
    public int SearchResponseHours { get; set; } = 24;

    /// <summary>How long screening decisions are kept. Screening runs at temperature 0, so a decision for the
    /// same paper, criteria, model and prompt version would come out the same; reusing it saves time and money.</summary>
    public int ScreeningDecisionDays { get; set; } = 90;

    /// <summary>Folder for the cache (relative paths are resolved against the content root).</summary>
    public string Folder { get; set; } = Path.Combine("App_Data", "cache");
}

/// <summary>
/// A small file cache for source search responses and screening decisions. Keys are SHA-256 hashes of
/// everything that determines the result (for screening: model, prompt version, criteria and the paper's
/// text), so a changed criterion or prompt never reuses an old answer. Every reuse is marked in the ledger.
/// </summary>
public class ReviewCache
{
    private readonly CacheOptions _options;
    private readonly string _root;
    private static ILogger _log => AppLog.For<ReviewCache>();

    public ReviewCache(CacheOptions options, string contentRoot)
    {
        _options = options;
        _root = Path.IsPathRooted(options.Folder) ? options.Folder : Path.Combine(contentRoot, options.Folder);
    }

    /// <summary>A cache that never stores anything (tests, tools).</summary>
    public static ReviewCache Disabled { get; } = new(new CacheOptions { Enabled = false }, Path.GetTempPath());

    public bool Enabled => _options.Enabled;

    public static string Key(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts.Select(p => p ?? ""))))).ToLowerInvariant();

    public bool TryGet<T>(string kind, string key, out T? value) where T : class
    {
        value = null;
        if (!_options.Enabled) return false;
        string path = PathFor(kind, key);
        try
        {
            if (!File.Exists(path)) return false;
            TimeSpan maxAge = kind == "search" ? TimeSpan.FromHours(_options.SearchResponseHours) : TimeSpan.FromDays(_options.ScreeningDecisionDays);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value != null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cache read failed for {Kind}/{Key}: {Message}", kind, key, ex.Message);
            return false;
        }
    }

    public void Set<T>(string kind, string key, T value)
    {
        if (!_options.Enabled) return;
        string path = PathFor(kind, key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cache write failed for {Kind}/{Key}: {Message}", kind, key, ex.Message);
        }
    }

    /// <summary>Deletes cache files older than the longest retention. Called by the cleanup worker.</summary>
    public int Prune()
    {
        if (!Directory.Exists(_root)) return 0;
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(_options.ScreeningDecisionDays, _options.SearchResponseHours / 24.0 + 1));
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); deleted++; } } catch { }
        }
        return deleted;
    }

    private string PathFor(string kind, string key) => Path.Combine(_root, kind, key[..2], key + ".json");
}

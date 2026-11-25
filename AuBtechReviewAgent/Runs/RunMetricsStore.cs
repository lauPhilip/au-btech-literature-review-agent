using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Metrics" section of appsettings.json.</summary>
public class MetricsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder of the metrics store (relative paths are resolved against the content root).</summary>
    public string Folder { get; set; } = Path.Join("App_Data", "metrics");

    /// <summary>An optional price table for the estimated cost of a run, on the Metrics page and in the downloads; off when empty (issues #414, #504).</summary>
    public MetricsPrices Prices { get; set; } = new();
}

/// <summary>
/// Append-only store of run metrics: one JSON object per line in runs.jsonl. It lives outside the run
/// folders, so it is not removed by the run clean-up, and it holds no query text or paper content.
/// </summary>
public class RunMetricsStore
{
    private readonly MetricsOptions _options;
    private readonly string _file;
    private static readonly object Lock = new();
    private static ILogger _log => AppLog.For<RunMetricsStore>();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public RunMetricsStore(MetricsOptions options, string contentRoot)
    {
        _options = options;
        string folder = Path.IsPathRooted(options.Folder) ? options.Folder : Path.Join(contentRoot, options.Folder);
        _file = Path.Join(folder, "runs.jsonl");
    }

    /// <summary>A store that keeps nothing (tests, tools).</summary>
    public static RunMetricsStore Disabled { get; } = new(new MetricsOptions { Enabled = false }, Path.GetTempPath());

    public bool Enabled => _options.Enabled;
    public string FilePath => _file;

    /// <summary>The price table for the estimated cost of a run (off when empty).</summary>
    public MetricsPrices Prices => _options.Prices;

    public void Append(RunMetrics metrics)
    {
        if (!_options.Enabled) return;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, JsonSerializer.Serialize(metrics, Json) + "\n");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run metrics not stored: {Message}", ex.Message);
        }
    }

    // The parsed file, kept until its length or write time changes (issue #306), so a page view does not parse it again.
    private (long Length, DateTime Written, RunMetrics[] Runs)? _parsed;

    /// <summary>
    /// Every stored run of one kind, oldest first: the systematic runs, or with <paramref name="multivocal"/> the
    /// multivocal ones, so the figures of the two kinds of review are never mixed. A line that cannot be read is skipped.
    /// The parsed lines are kept while the file's length and write time stay the same, and a new run changes both, so it
    /// shows at once. Callers get a new list each time and must not change the runs in it, which are shared.
    /// </summary>
    public List<RunMetrics> ReadAll(bool multivocal = false)
    {
        if (!_options.Enabled) return new List<RunMetrics>();
        return Parsed().Where(m => m.IsMultivocal == multivocal).OrderBy(m => m.CompletedUtc, StringComparer.Ordinal).ToList();
    }

    private RunMetrics[] Parsed()
    {
        lock (Lock)
        {
            var info = new FileInfo(_file);
            if (!info.Exists) return Array.Empty<RunMetrics>();
            if (_parsed is { } kept && kept.Length == info.Length && kept.Written == info.LastWriteTimeUtc) return kept.Runs;

            var runs = Parse(File.ReadAllLines(_file));
            _parsed = (info.Length, info.LastWriteTimeUtc, runs);
            return runs;
        }
    }

    private RunMetrics[] Parse(string[] lines)
    {
        var list = new List<RunMetrics>(lines.Length);
        int skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<RunMetrics>(line) is { } m) list.Add(m); }
            catch (JsonException)
            {
                // A damaged line (for example from an interrupted write) is skipped so the rest stays readable.
                skipped++;
            }
        }
        if (skipped > 0) _log.LogWarning("Skipped {Count} unreadable line(s) in {File}", skipped, _file);
        return list.ToArray();
    }
}

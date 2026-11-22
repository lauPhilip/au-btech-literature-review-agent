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

    /// <summary>
    /// Every stored run of one kind, oldest first: the systematic runs, or with <paramref name="multivocal"/> the
    /// multivocal ones, so the figures of the two kinds of review are never mixed. A line that cannot be read is skipped.
    /// </summary>
    public List<RunMetrics> ReadAll(bool multivocal = false)
    {
        var list = new List<RunMetrics>();
        if (!_options.Enabled || !File.Exists(_file)) return list;
        int skipped = 0;
        string[] lines;
        lock (Lock) lines = File.ReadAllLines(_file);
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
        return list.Where(m => m.IsMultivocal == multivocal).OrderBy(m => m.CompletedUtc, StringComparer.Ordinal).ToList();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

public static class JournalRankingMatcher
{
    // Each yearly Scimago file is ~11 MB. It used to be scanned line by line for every paper; now each year
    // is read once into a title -> quartile dictionary and reused by every run.
    private static readonly ConcurrentDictionary<int, Lazy<IReadOnlyDictionary<string, string>>> Cache = new();

    public static string LookupQuartile(string journalName, int year)
    {
        if (string.IsNullOrWhiteSpace(journalName)) return "N/A";

        // Fallback gracefully to nearest available boundaries if the paper year is out of bounds
        // (year 0 = unknown publication year -> use the newest dataset).
        int targetYear = year <= 0 ? 2025 : Math.Clamp(year, 2020, 2025);
        string wanted = Normalize(journalName);
        if (wanted.Length == 0) return "N/A";

        var table = Cache.GetOrAdd(targetYear, y => new Lazy<IReadOnlyDictionary<string, string>>(() => LoadYear(y))).Value;
        return table.TryGetValue(wanted, out var quartile) ? quartile : "N/A";
    }

    private static IReadOnlyDictionary<string, string> LoadYear(int year)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        string? csvPath = FindDatasetPath(year);
        if (csvPath == null) return table;

        try
        {
            // Scimago CSV column headers typically contain "Title" and "SJR Best Quartile"
            using var reader = new StreamReader(csvPath);
            string headerLine = reader.ReadLine() ?? "";
            var headers = headerLine.Split(';').Select(h => h.Trim('"')).ToList();

            int titleIdx = headers.FindIndex(h => h.Equals("Title", StringComparison.OrdinalIgnoreCase));
            int quartileIdx = headers.FindIndex(h => h.Contains("Quartile", StringComparison.OrdinalIgnoreCase));
            if (titleIdx == -1 || quartileIdx == -1) return table;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var tokens = line.Split(';');
                if (tokens.Length <= Math.Max(titleIdx, quartileIdx)) continue;

                // Exact match on the normalised title only (no "contains" matching - that matched short titles
                // such as "Nature" inside unrelated venue names). The first (highest-ranked) row wins.
                string quartileValue = tokens[quartileIdx].Trim('"');
                if (!Regex.IsMatch(quartileValue, "^Q[1-4]$")) continue;
                table.TryAdd(Normalize(tokens[titleIdx]), quartileValue);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scimago] Could not read {csvPath}: {ex.Message}");
        }
        return table;
    }

    /// <summary>Lower-case, "&amp;" -> "and", punctuation stripped, whitespace collapsed.</summary>
    public static string Normalize(string? venue)
    {
        string v = (venue ?? "").Trim('"').ToLowerInvariant().Replace("&", " and ");
        v = Regex.Replace(v, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(v, @"\s+", " ").Trim();
    }

    // The data ships as "ScimagoData/scimagojr 2025.csv"; the lookup used to ask for "scimago_2025.csv",
    // so it never found a file and every quartile silently came back as N/A. Accept both names, and look
    // next to the binaries as well as in the content root (dotnet watch / dotnet run use the project dir).
    private static string? FindDatasetPath(int year)
    {
        var roots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.Distinct();
        var names = new[] { $"scimagojr {year}.csv", $"scimago_{year}.csv" };
        foreach (var root in roots)
            foreach (var name in names)
            {
                string path = Path.Combine(root, "ScimagoData", name);
                if (File.Exists(path)) return path;
            }
        return null;
    }
}

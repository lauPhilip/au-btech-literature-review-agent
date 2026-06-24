using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

public static class JournalRankingMatcher
{
    public static string LookupQuartile(string journalName, int year)
    {
        if (string.IsNullOrWhiteSpace(journalName)) return "N/A";

        // Fallback gracefully to nearest available boundaries if the paper year is out of bounds
        int targetYear = Math.Clamp(year, 2020, 2025);
        string csvPath = Path.Combine(AppContext.BaseDirectory, "ScimagoData", $"scimago_{targetYear}.csv");

        if (!File.Exists(csvPath)) return "N/A";

        try
        {
            // Scimago CSV column headers typically contain "Title" and "SJR Best Quartile"
            using var reader = new StreamReader(csvPath);
            string headerLine = reader.ReadLine() ?? "";
            var headers = headerLine.Split(';').Select(h => h.Trim('"')).ToList();
            
            int titleIdx = headers.FindIndex(h => h.Equals("Title", StringComparison.OrdinalIgnoreCase));
            int quartileIdx = headers.FindIndex(h => h.Contains("Quartile", StringComparison.OrdinalIgnoreCase));

            if (titleIdx == -1 || quartileIdx == -1) return "N/A";

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var tokens = line.Split(';');
                if (tokens.Length <= Math.Max(titleIdx, quartileIdx)) continue;

                string currentTitle = tokens[titleIdx].Trim('"');
                if (currentTitle.Equals(journalName, StringComparison.OrdinalIgnoreCase) || 
                    journalName.Contains(currentTitle, StringComparison.OrdinalIgnoreCase))
                {
                    string quartileValue = tokens[quartileIdx].Trim('"');
                    return !string.IsNullOrWhiteSpace(quartileValue) ? quartileValue : "N/A";
                }
            }
        }
        catch
        {
            // Fallback safe default on stream locks
        }

        return "N/A";
    }
}
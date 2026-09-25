using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>One record from a labelled screening dataset (e.g. an ASReview / SYNERGY CSV).</summary>
public record LabelledRecord(string Id, string Title, string Abstract, bool Included, string Year = "", string Authors = "", string Doi = "");

/// <summary>Confusion matrix of screening decisions against a human gold standard, with the usual metrics.</summary>
public record ScreeningConfusion(double TruePositives, double FalsePositives, double TrueNegatives, double FalseNegatives)
{
    public double Total => TruePositives + FalsePositives + TrueNegatives + FalseNegatives;

    /// <summary>Share of truly relevant papers the model included. The metric that matters most in SLR screening.</summary>
    public double Recall => Ratio(TruePositives, TruePositives + FalseNegatives);
    public double Specificity => Ratio(TrueNegatives, TrueNegatives + FalsePositives);
    public double Precision => Ratio(TruePositives, TruePositives + FalsePositives);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
    public double Accuracy => Ratio(TruePositives + TrueNegatives, Total);

    /// <summary>Agreement between model and human beyond chance (0 = chance, 1 = perfect).</summary>
    public double CohensKappa
    {
        get
        {
            if (Total == 0) return 0;
            double observed = Accuracy;
            double pYes = (TruePositives + FalsePositives) / Total * ((TruePositives + FalseNegatives) / Total);
            double pNo = (TrueNegatives + FalseNegatives) / Total * ((TrueNegatives + FalsePositives) / Total);
            double expected = pYes + pNo;
            return expected >= 1 ? 1 : (observed - expected) / (1 - expected);
        }
    }

    private static double Ratio(double a, double b) => b == 0 ? 0 : a / b;

    public static ScreeningConfusion From(IEnumerable<(bool Gold, bool Predicted)> pairs, double excludedWeight = 1.0)
    {
        double tp = 0, fp = 0, tn = 0, fn = 0;
        foreach (var (gold, predicted) in pairs)
        {
            if (gold && predicted) tp++;
            else if (gold) fn++;
            else if (predicted) fp += excludedWeight;
            else tn += excludedWeight;
        }
        return new ScreeningConfusion(tp, fp, tn, fn);
    }
}

/// <summary>
/// Helpers for the screening evaluation tool (tools/ScreeningEval): reading a labelled dataset and drawing
/// a reproducible sample. Kept in the main project so they are unit-tested with the rest.
/// </summary>
public static class ScreeningEvaluation
{
    /// <summary>
    /// Reads an ASReview-style CSV: columns "title", "abstract" and a label column ("label_included",
    /// "included" or "label", with 1/0, true/false or yes/no). Optional: "record_id"/"id", "year", "authors", "doi".
    /// The SYNERGY datasets (github.com/asreview/synergy-dataset) export in this format.
    /// </summary>
    public static List<LabelledRecord> LoadCsv(string csvText)
    {
        var rows = ParseCsv(csvText);
        if (rows.Count < 2) throw new FormatException("The CSV has no data rows.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);

        int title = Col("title", "primary_title");
        int abs = Col("abstract", "notes_abstract");
        int label = Col("label_included", "included", "label", "label_abstract_included");
        int id = Col("record_id", "id", "openalex_id");
        int year = Col("year", "publication_year");
        int authors = Col("authors", "author");
        int doi = Col("doi");
        if (title < 0 || label < 0) throw new FormatException("The CSV needs at least a 'title' column and a label column ('label_included', 'included' or 'label').");

        var records = new List<LabelledRecord>();
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Get(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (!TryParseLabel(Get(label), out bool included)) continue; // unlabelled rows are skipped
            records.Add(new LabelledRecord(
                id >= 0 && Get(id).Length > 0 ? Get(id) : $"row{r}",
                Get(title), Get(abs), included, Get(year), Get(authors), Get(doi)));
        }
        return records;
    }

    public static bool TryParseLabel(string value, out bool included)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1": case "1.0": case "true": case "yes": case "y": case "included": included = true; return true;
            case "0": case "0.0": case "false": case "no": case "n": case "excluded": included = false; return true;
            default: included = false; return false;
        }
    }

    /// <summary>
    /// Keeps every relevant record and at most <paramref name="maxExcluded"/> randomly chosen irrelevant ones
    /// (fixed seed, so the same sample is drawn every time). Relevant papers are rare in real screening data,
    /// so this keeps the cost down while recall stays exact. The returned weight scales the excluded counts
    /// back up to the full dataset when estimating precision and specificity.
    /// </summary>
    public static (List<LabelledRecord> Sample, double ExcludedWeight) Sample(IReadOnlyList<LabelledRecord> records, int? maxExcluded, int seed = 42)
    {
        var included = records.Where(r => r.Included).ToList();
        var excluded = records.Where(r => !r.Included).ToList();
        if (maxExcluded == null || excluded.Count <= maxExcluded.Value) return (records.ToList(), 1.0);

        var rng = new Random(seed);
        var chosen = excluded.OrderBy(_ => rng.Next()).Take(Math.Max(0, maxExcluded.Value)).ToList();
        double weight = chosen.Count == 0 ? 1.0 : excluded.Count / (double)chosen.Count;
        return (included.Concat(chosen).ToList(), weight);
    }

    /// <summary>RFC 4180 CSV parser: quoted fields, doubled quotes, commas and newlines inside quotes.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }

    public static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}

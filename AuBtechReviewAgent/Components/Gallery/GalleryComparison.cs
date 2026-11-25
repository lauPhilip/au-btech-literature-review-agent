using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>One line of the comparison of two versions: what is compared, its value in each, and what changed.</summary>
public sealed record ComparisonRow(string Label, string From, string To, string Change);

/// <summary>
/// The side-by-side comparison of two versions of a review in the gallery, computed in code from the files each
/// version keeps (entry.json's figures, the copied ledger for the themes, run-metrics.json for the effort). No model is
/// asked anything. A figure a version did not record shows as "not recorded".
/// </summary>
public static class GalleryComparison
{
    public const string NotRecorded = "not recorded";
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ComparisonRow> Rows(GalleryEntry from, string fromFolder, GalleryEntry to, string toFolder, MetricsPrices? prices = null)
    {
        var rows = new List<ComparisonRow>
        {
            Text("Finished on", GalleryCard.Date(from.CompletedUtc), GalleryCard.Date(to.CompletedUtc)),
            Text("TraceableAI version", ToolVersion.Display(from.ToolVersion), ToolVersion.Display(to.ToolVersion)),
            Count("Records found", from.Identified, to.Identified),
            Count("Studies included", from.Included, to.Included),
            Count("References", from.References, to.References),
            Count("Themes", from.Themes, to.Themes),
            Count("Citations checked", from.Citations.Checked, to.Citations.Checked),
            Share("Supported", from.Citations, from.Citations.Supported, to.Citations, to.Citations.Supported),
            Share("Partly supported", from.Citations, from.Citations.Partly, to.Citations, to.Citations.Partly),
            Share("Not supported", from.Citations, from.Citations.NotSupported, to.Citations, to.Citations.NotSupported),
        };

        var (fromThemes, toThemes) = (ThemeNames(fromFolder), ThemeNames(toFolder));
        if (fromThemes.Count + toThemes.Count > 0)
        {
            var added = toThemes.Where(t => !fromThemes.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            var dropped = fromThemes.Where(t => !toThemes.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            string change = added.Count + dropped.Count == 0 ? "same" : string.Join("; ", new[]
            {
                added.Count > 0 ? "new: " + string.Join(", ", added) : null,
                dropped.Count > 0 ? "gone: " + string.Join(", ", dropped) : null,
            }.Where(p => p != null));
            rows.Add(new("Theme names", List(fromThemes), List(toThemes), change));
        }

        var (fromRun, toRun) = (Metrics(fromFolder), Metrics(toFolder));
        rows.Add(Optional("Model calls", fromRun?.LlmCalls, toRun?.LlmCalls));
        rows.Add(Optional("Tokens", fromRun?.Tokens, toRun?.Tokens));
        rows.Add(Optional("Minutes", fromRun == null ? null : (long)Math.Round(fromRun.DurationMinutes), toRun == null ? null : (long)Math.Round(toRun.DurationMinutes)));
        if (prices is { Enabled: true })
        {
            double? a = fromRun == null ? null : prices.Cost(fromRun), b = toRun == null ? null : prices.Cost(toRun);
            rows.Add(new("Estimated cost", a is double x ? prices.Format(x) : NotRecorded, b is double y ? prices.Format(y) : NotRecorded,
                a is double c && b is double d ? (Math.Abs(d - c) < 0.005 ? "same" : (d > c ? "+" : "−") + prices.Format(Math.Abs(d - c))) : "–"));
        }
        return rows;
    }

    /// <summary>The theme names in the copied ledger, in the order of the codebook.</summary>
    public static IReadOnlyList<string> ThemeNames(string folder) =>
        (GallerySnapshot.LoadLedger(folder)?.ThematicSynthesis?.Themes ?? new())
            .Select(t => t.Name.Trim()).Where(n => n.Length is > 0 and <= 120).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static RunMetrics? Metrics(string folder)
    {
        string path = Path.Join(folder, "run-metrics.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RunMetrics>(File.ReadAllText(path), Read); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static ComparisonRow Text(string label, string a, string b) => new(label, a, b, a == b ? "same" : "changed");

    private static ComparisonRow Count(string label, long a, long b) => new(label, N(a), N(b), Delta(a, b));

    private static ComparisonRow Optional(string label, long? a, long? b) =>
        new(label, a is long x ? N(x) : NotRecorded, b is long y ? N(y) : NotRecorded, a is long c && b is long d ? Delta(c, d) : "–");

    private static ComparisonRow Share(string label, CitationShares fa, int a, CitationShares fb, int b)
    {
        string Show(CitationShares s, int n) => s.HasVerdicts ? $"{N(n)} ({s.Percent(n)}%)" : NotRecorded;
        string change = fa.HasVerdicts && fb.HasVerdicts
            ? (fb.Percent(b) - fa.Percent(a)) is var points && points == 0 ? "same share" : $"{(points > 0 ? "+" : "−")}{Math.Abs(points)} points"
            : "–";
        return new(label, Show(fa, a), Show(fb, b), change);
    }

    private static string Delta(long a, long b) => b == a ? "same" : (b > a ? "+" : "−") + N(Math.Abs(b - a));

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string List(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);
}

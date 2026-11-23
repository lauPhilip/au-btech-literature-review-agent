using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AuBtechReviewAgent;

/// <summary>
/// The Metrics page's figures as files (issue #416): /metrics/runs.csv, /metrics/runs.json and /metrics/stages.csv
/// (<see cref="MetricsExport"/>). Public, like the page: they hold what the page shows and more of the same kind,
/// never a research question, paper text or a whole run id. Multivocal runs are in them only while that review is shown
/// on the site, as on the page. The services are passed in here, not taken as handler parameters (see WorkspaceDownloads).
/// </summary>
public static class MetricsDownloads
{
    public const string RunsCsv = "/metrics/runs.csv", RunsJson = "/metrics/runs.json", StagesCsv = "/metrics/stages.csv";

    public static void MapMetricsDownloads(this WebApplication app, RunMetricsStore store, ReviewModulesOptions modules, MetricsPrices prices)
    {
        app.MapGet(RunsCsv, (HttpContext http) => File(http, MetricsExport.RunsCsv(Runs(store, modules), prices), "text/csv", "traceableai-runs.csv"));
        app.MapGet(RunsJson, (HttpContext http) => File(http, MetricsExport.RunsJson(Runs(store, modules), prices), "application/json", "traceableai-runs.json"));
        app.MapGet(StagesCsv, (HttpContext http) => File(http, MetricsExport.StagesCsv(Runs(store, modules)), "text/csv", "traceableai-stages.csv"));
    }

    /// <summary>The finished runs of every kind the site shows: the systematic ones, and the multivocal ones while that review is shown.</summary>
    public static IReadOnlyList<RunMetrics> Runs(RunMetricsStore store, ReviewModulesOptions modules) =>
        store.ReadAll().Concat(modules.Shows("grey") ? store.ReadAll(multivocal: true) : Enumerable.Empty<RunMetrics>()).ToList();

    private static IResult File(HttpContext http, string text, string type, string name)
    {
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        return Results.Text(text, type + "; charset=utf-8", Encoding.UTF8);
    }
}

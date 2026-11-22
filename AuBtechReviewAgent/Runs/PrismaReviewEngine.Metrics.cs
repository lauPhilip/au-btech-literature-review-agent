using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>
    /// Computes run-metrics.json from the run's own files and appends it to the metrics store. Never fails
    /// the run: metrics are a by-product.
    /// </summary>
    private async Task WriteRunMetricsAsync(RunContext ctx)
    {
        try
        {
            PrismaReport? report = null;
            string reportPath = GetReportFilePath(ctx.RunId);
            if (File.Exists(reportPath))
                report = JsonSerializer.Deserialize<PrismaReport>(await File.ReadAllTextAsync(reportPath));

            var checks = new List<CitationSupportResult>();
            CitationSupportSummary? initial = null;
            int stripped = 0;
            string auditPath = Path.Join(ctx.Workspace, "citation-audit.json");
            if (File.Exists(auditPath))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("SupportChecks", out var c)) checks = JsonSerializer.Deserialize<List<CitationSupportResult>>(c.GetRawText()) ?? new();
                if (root.TryGetProperty("InitialSupportSummary", out var i)) initial = JsonSerializer.Deserialize<CitationSupportSummary>(i.GetRawText());
                if (root.TryGetProperty("InvalidMarkersStripped", out var st) && st.TryGetInt32(out int n)) stripped = n;
            }

            var settings = new Dictionary<string, string>
            {
                ["Synthesis.ThematicSynthesis"] = Synthesis.ThematicSynthesis.ToString(),
                ["Synthesis.DualCoding"] = Synthesis.DualCoding.ToString(),
                ["Synthesis.RepairCitations"] = Synthesis.RepairCitations.ToString(),
                ["Synthesis.SecondCitationCheck"] = Synthesis.SecondCitationCheck.ToString(),
                ["Synthesis.MaxThemes"] = Synthesis.MaxThemes.ToString(),
                ["DualScreening"] = ctx.Request.DualScreening.ToString(),
                ["CitationChaining"] = ctx.Request.CitationChaining.ToString(),
                ["PeerReviewOnly"] = ctx.Request.PeerReviewOnly.ToString(),
            };
            var metrics = RunMetrics.Build(ctx.RunId, ctx.State, report, checks, initial, stripped, ctx.Chat.Calls, settings);
            await File.WriteAllTextAsync(Path.Join(ctx.Workspace, "run-metrics.json"),
                JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            MetricsStore.Append(metrics);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run {RunId}: run metrics not written: {Message}", ctx.RunId, SanitizeLogMessage(ex.Message));
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The retraction check of a systematic review: before the full texts are read, the included studies' DOIs are checked.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// Checks the included studies at Crossref (Report/RetractionCheck.cs). A retracted study is excluded, with the notice
    /// as its reason in the screening ledger; a correction, an expression of concern or a registered published version of
    /// a preprint is kept for Table A.1. Uses the reference enrichment's fetch, so a run with fake sources checks nothing
    /// unless a test sets <see cref="ReferenceLookup"/>. A failure leaves the studies as they are and the run goes on.
    /// </summary>
    private async Task CheckRetractionsAsync(RunContext ctx)
    {
        if (ReferenceLookup == null && SourceFactory != null) return;
        var screening = ctx.State.Phases.Screening;
        var included = screening.Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase)).ToList();
        if (included.Count == 0) return;
        try
        {
            var check = await RetractionCheck.CheckAsync(included, ctx.Workspace, ReferenceLookup ?? DefaultReferenceLookup,
                url => OpenSourceHttp.WithMailto(url, OpenSources.ContactEmail));
            foreach (var entry in check.Studies.Where(e => e.Status == RetractionStatus.Retracted))
            {
                int i = screening.FindIndex(p => p.PaperId == entry.PaperId && p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                screening[i] = screening[i] with
                {
                    Decision = "Excluded",
                    ExclusionReason = ExclusionReasons.Retracted,
                    Reasoning = RetractionCheckReason(entry) + " Screening had included it: " + screening[i].Reasoning,
                };
                ctx.State.Stats.Included--;
                ctx.State.Stats.Excluded++;
            }
            ctx.State.RetractionCheck = check;
            await File.WriteAllTextAsync(Path.Join(ctx.Workspace, RetractionCheckFile.FileName),
                JsonSerializer.Serialize(check, new JsonSerializerOptions { WriteIndented = true }));
            await SaveStateAsync(ctx.RunId, ctx.State);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            _log.LogWarning("Run {RunId}: retraction check failed: {Message}", ctx.RunId, SanitizeLogMessage(ex.Message));
        }
    }

    /// <summary>"Excluded after screening: Crossref lists a retraction of this DOI (notice 10.1/r, Retraction Watch, 2010-02-06)."</summary>
    internal static string RetractionCheckReason(RetractionCheckEntry entry)
    {
        var notice = entry.Notices.First(n => RetractionCheck.StatusOf(new[] { n }) == RetractionStatus.Retracted);
        string details = string.Join(", ", new[] { notice.NoticeDoi.Length > 0 ? "notice " + notice.NoticeDoi : "", RetractionCheckText.SourceName(notice.Source), notice.Date }
            .Where(s => s.Length > 0));
        return $"Excluded after screening: Crossref lists a {notice.Type.Replace('_', ' ')} of this DOI{(details.Length > 0 ? $" ({details})" : "")}.";
    }
}

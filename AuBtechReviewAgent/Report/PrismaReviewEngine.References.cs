using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The reference enrichment of a systematic review: before the references are numbered, gaps in them are filled.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// Test hook: fetches an address for the reference enrichment (default: Crossref and OpenAlex through OpenSourceHttp,
    /// arXiv through its spaced gate). A run whose sources are fakes (<see cref="SourceFactory"/>) has nothing real to look
    /// up, so it is not enriched unless this is set.
    /// </summary>
    public Func<string, Task<string>>? ReferenceLookup { get; init; }

    private async Task EnrichReferencesAsync(RunContext ctx)
    {
        if (ReferenceLookup == null && SourceFactory != null) return;
        var screening = ctx.State.Phases.Screening;
        var included = screening.Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase)).ToList();
        if (included.Count == 0) return;
        try
        {
            var enricher = new ReferenceEnricher(ReferenceLookup ?? DefaultReferenceLookup, OpenSources.ContactEmail, OpenSources.OpenAlexApiKey);
            var (logs, audit) = await enricher.EnrichAsync(included, ctx.Workspace);
            foreach (var log in logs)
            {
                int i = screening.FindIndex(p => p.PaperId == log.PaperId && p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase));
                if (i >= 0) screening[i] = log;
            }
            await File.WriteAllTextAsync(Path.Join(ctx.Workspace, ReferenceEnrichmentFile.FileName),
                JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // The references stay as the sources gave them; the run goes on.
            _log.LogWarning("Run {RunId}: reference enrichment failed: {Message}", ctx.RunId, SanitizeLogMessage(ex.Message));
        }
    }

    /// <summary>
    /// The reference enrichment's fetch on the server: Crossref and OpenAlex through OpenSourceHttp, arXiv through its spaced
    /// gate. The multivocal review's formal pool uses the same one (Program.cs).
    /// </summary>
    internal static Task<string> DefaultReferenceLookup(string url) =>
        url.StartsWith("https://export.arxiv.org/", StringComparison.Ordinal)
            ? ArxivSource.GetAsync(url)
            : OpenSourceHttp.GetStringAsync(url, maxAttempts: 3);
}

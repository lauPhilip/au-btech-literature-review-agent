using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The reference enrichment of the formal pool, after the extraction and before the references are written.</summary>
public sealed partial class MultivocalRunner
{
    /// <summary>
    /// Fetches an address for the reference enrichment of the formal pool (Crossref, OpenAlex, arXiv). Null, as in the
    /// tests unless a test sets it, leaves the formal references as the databases gave them.
    /// </summary>
    public Func<string, Task<string>>? ReferenceLookup { get; init; }

    /// <summary>The polite-pool contact e-mail and the OpenAlex key for the enrichment; never written to its file.</summary>
    public OpenSourcesOptions ReferenceContact { get; init; } = new();

    /// <summary>
    /// Completes the extracted academic sources' references (<see cref="MultivocalReferenceEnrichment"/>). A failure
    /// leaves them as the databases gave them, and the run goes on, as in the systematic review.
    /// </summary>
    private async Task EnrichReferencesAsync(Guid runId)
    {
        if (ReferenceLookup == null) return;
        var ledger = _searcher.LoadLedger(runId);
        var extraction = _extractor.Load(runId);
        if (ledger?.HasFormalPool != true || extraction == null) return;
        try
        {
            await MultivocalReferenceEnrichment.RunAsync(_runs.FolderOf(runId), ledger, extraction, ReferenceLookup,
                ReferenceContact.ContactEmail, ReferenceContact.OpenAlexApiKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException)
        {
            _log.LogWarning("Multivocal run {RunId}: reference enrichment failed: {Message}", runId, ex.Message.Replace('\r', ' ').Replace('\n', ' '));
        }
    }
}

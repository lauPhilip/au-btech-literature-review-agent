using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The reference enrichment of a multivocal run's formal pool (MLR I-6), the same step the systematic review takes
/// before it numbers its references (<see cref="ReferenceEnricher"/>): each extracted academic source with a DOI or an
/// arXiv number is looked up at Crossref, OpenAlex or arXiv, its answers are saved in SourceResponses/ and reused, an
/// answer whose title names another work is refused, and no model is called. Grey sources are not touched. The ledger
/// keeps the record as the database gave it; the completed fields are kept in reference-enrichment.json, which
/// <see cref="MultivocalSearcher.LoadLedger"/> attaches to the ledger, and the formal references are cited from there.
/// </summary>
public static class MultivocalReferenceEnrichment
{
    /// <summary>
    /// Looks up the extracted formal sources of the run, writes reference-enrichment.json to its folder and attaches it to
    /// <paramref name="ledger"/>; returns the file, or null when the run has no extracted formal source.
    /// </summary>
    public static async Task<ReferenceEnrichmentFile?> RunAsync(string folder, MultivocalLedger ledger, MultivocalExtractionFile extraction,
        Func<string, Task<string>> get, string contactEmail = "", string openAlexKey = "")
    {
        var numbers = MultivocalReporter.SourceNumbers(extraction);
        var papers = ledger.FormalPool
            .Where(s => numbers.ContainsKey(MultivocalSearcher.AddressKey(s.Record.Url)))
            .Select(s => s.Paper!)
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (papers.Count == 0) return null;

        var (_, audit) = await new ReferenceEnricher(get, contactEmail, openAlexKey).EnrichAsync(papers.Select(AsRecord).ToList(), folder);
        await SafeFile.WriteAllTextAsync(Path.Join(folder, ReferenceEnrichmentFile.FileName),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        ledger.References = audit;
        return audit;
    }

    /// <summary>The academic source as the systematic review's record of an included study, for the enrichment.</summary>
    private static ScreeningLog AsRecord(AcademicPaper paper)
    {
        var apa = ApaCitationBuilder.Build(paper);
        return new ScreeningLog(paper.Id, paper.Title ?? "", "Included", "", apa.Citation, "", apa.VenueType, apa.VenueName, apa.Year,
            paper.Authors ?? new List<string>(), PrismaReviewEngine.NormalizeDoi(paper.Doi), paper.Url, Details: apa.Details);
    }
}

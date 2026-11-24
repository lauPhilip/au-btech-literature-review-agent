using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The counts of one pool of a multivocal review, from the search to the extraction, counted in code from the run's
/// files (block I-6): the run view shows the grey and the formal pool side by side with them, and the flow diagram
/// draws one arm for each.
/// </summary>
/// <param name="Identified">Records the searches returned: the grey sources found (already one per address), or every academic record.</param>
/// <param name="DuplicatesRemoved">Academic records removed as duplicates by id, DOI or title, before screening.</param>
/// <param name="NoAddressRemoved">Academic records removed before screening because they had no web address to cite.</param>
/// <param name="DuplicatesAtScreening">Sources screened once under another address with the same title.</param>
/// <param name="NotScreened">Sources whose screening failed.</param>
/// <param name="Kept">Grey sources that passed the quality threshold, or academic sources appraised with MMAT.</param>
/// <param name="BelowThreshold">Grey sources below the quality threshold (none for the formal pool).</param>
/// <param name="AbstractOnly">Of the extracted academic sources, those that rest on their abstract alone (decision 8).</param>
/// <param name="ViaSeeds">Of the academic records identified, those found by building out from the most-cited included sources.</param>
/// <param name="Second">The pool's second screenings: how many, why, the audit sample and its κ; null before screening.</param>
public sealed record PoolCounts(
    int Identified, int DuplicatesRemoved, int NoAddressRemoved, int DuplicatesAtScreening, int NotScreened,
    int Included, int Excluded, int ToLookAt, double? Kappa,
    int Kept, int BelowThreshold, int NotAssessed, int Extracted, int NotExtracted, int AbstractOnly,
    SecondScreeningCounts? Second = null, int ViaSeeds = 0)
{
    public int Screened => Included + Excluded;
}

public static class MultivocalPools
{
    /// <summary>The counts of the grey pool (<paramref name="formal"/> false) or the formal pool; a step not run yet counts 0.</summary>
    public static PoolCounts For(bool formal, MultivocalLedger ledger, MultivocalScreeningFile? screening, MultivocalQualityFile? quality,
        MultivocalExtractionFile? extraction)
    {
        var removed = formal ? ledger.FormalRemoved ?? new List<RemovedRecord>() : new List<RemovedRecord>();
        int pool = (formal ? ledger.FormalPool : ledger.GreyPool).Count();
        var screened = (screening?.Sources ?? new List<GreyScreening>()).Where(s => s.Formal == formal).ToList();
        var assessed = (quality?.Sources ?? new List<GreyQuality>()).Where(q => q.Formal == formal).ToList();
        var extracted = (extraction?.Sources ?? new List<GreyExtraction>()).Where(x => x.Formal == formal).ToList();
        return new PoolCounts(
            Identified: pool + removed.Count,
            DuplicatesRemoved: removed.Count(r => r.Reason == RemovedRecord.Duplicate),
            NoAddressRemoved: removed.Count(r => r.Reason == MultivocalSearcher.NoWebAddress),
            DuplicatesAtScreening: screened.Count(s => s.Decision == "Duplicate"),
            NotScreened: screened.Count(s => s.Decision == "Error"),
            Included: screened.Count(s => s.Decision == "Included"),
            Excluded: screened.Count(s => s.Decision == "Excluded"),
            ToLookAt: screened.Count(s => s.NeedsLook),
            Kappa: screening == null ? null : formal ? screening.KappaFormal : screening.KappaGrey ?? screening.Kappa,
            Kept: assessed.Count(q => q.Outcome == "Passed"),
            BelowThreshold: assessed.Count(q => q.Outcome == "Below threshold"),
            NotAssessed: assessed.Count(q => q.Outcome == "Not assessed"),
            Extracted: extracted.Count(x => x.Error == null),
            NotExtracted: extracted.Count(x => x.Error != null),
            AbstractOnly: extracted.Count(x => x.Error == null && x.TextBasis == MultivocalPages.AbstractBasis),
            Second: screening == null ? null : MultivocalSecondScreening.Count(screening, formal),
            ViaSeeds: formal ? ledger.SeedChaining?.Identified ?? 0 : 0);
    }
}

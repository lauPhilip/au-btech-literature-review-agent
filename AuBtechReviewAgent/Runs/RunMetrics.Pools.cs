using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The figures of one pool of a multivocal run (issue #415): the grey sources, or the academic records of the formal
/// pool. Counted in code from the run's files by <c>MultivocalRunLog.BuildMetrics</c>, from the same counts as the run
/// view and the flow diagram (<c>MultivocalPools.For</c>).
/// </summary>
public class PoolMetrics
{
    public int Identified { get; set; }
    public int DuplicatesRemoved { get; set; }
    public int NoAddressRemoved { get; set; }
    public int DuplicatesAtScreening { get; set; }
    public int NotScreened { get; set; }
    public int Screened { get; set; }
    public int Included { get; set; }
    public int Excluded { get; set; }

    /// <summary>Cohen's κ between the two screenings, over <see cref="KappaBasis"/>; null when no source was screened twice.</summary>
    public double? Kappa { get; set; }

    /// <summary>"audit sample" (a run screened from 23 Nov 2026: κ over the random audit sample only) or "all sources" (an older run, every source screened twice).</summary>
    public string? KappaBasis { get; set; }

    /// <summary>Sources screened twice: the targeted ones and the audit sample together.</summary>
    public int ScreenedTwice { get; set; }

    /// <summary>Sources in the random audit sample, the sources κ is computed over.</summary>
    public int AuditSample { get; set; }

    /// <summary>Sources screened again for a reason (included, unsure or flagged); their agreement is never in κ.</summary>
    public int TargetedSecond { get; set; }

    /// <summary>Of <see cref="TargetedSecond"/>, those whose two screenings agreed.</summary>
    public int TargetedAgreed { get; set; }

    /// <summary>Grey sources at or above the quality threshold, or academic sources appraised with MMAT (all kept).</summary>
    public int Kept { get; set; }
    public int BelowThreshold { get; set; }
    public int NotAssessed { get; set; }
    public int Extracted { get; set; }
    public int NotExtracted { get; set; }

    /// <summary>Extracted academic sources read on their abstract alone (0 for grey sources).</summary>
    public int AbstractOnly { get; set; }

    /// <summary>Grey pool: sources scored on the Table 7 checklist (passed or below the threshold); null for the formal pool.</summary>
    public int? Table7Scored { get; set; }

    /// <summary>Grey pool: the mean Table 7 points of the scored sources; null when none was scored or for the formal pool.</summary>
    public double? MeanTable7Points { get; set; }

    /// <summary>Formal pool: sources with an MMAT appraisal; null for the grey pool.</summary>
    public int? MmatAppraised { get; set; }

    /// <summary>Formal pool: of <see cref="MmatAppraised"/>, those appraised as empirical studies (MMAT applies to them).</summary>
    public int? MmatEmpirical { get; set; }
}

public partial class RunMetrics
{
    /// <summary>A multivocal run's grey pool; null for a systematic run and in older lines.</summary>
    public PoolMetrics? GreyPool { get; set; }

    /// <summary>A multivocal run's formal pool (academic databases); null without one, for a systematic run and in older lines.</summary>
    public PoolMetrics? FormalPool { get; set; }

    /// <summary>
    /// What <see cref="ScreeningKappa"/> is computed over: "all records" for a systematic run with two screenings,
    /// "audit sample" or "all sources" for a multivocal run (see <see cref="PoolMetrics.KappaBasis"/>); null in older lines.
    /// </summary>
    public string? ScreeningKappaBasis { get; set; }

    /// <summary>The grey and the formal pool, those the run has, keyed "grey" and "formal".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<(string Key, PoolMetrics Pool)> Pools
    {
        get
        {
            if (GreyPool != null) yield return ("grey", GreyPool);
            if (FormalPool != null) yield return ("formal", FormalPool);
        }
    }
}

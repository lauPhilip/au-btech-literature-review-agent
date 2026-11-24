using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// Which sources of a multivocal run are screened a second time (approved on 23 Nov 2026 to cut the cost of screening,
/// following the rapid-review practice of single screening with a verified sample). Every source is screened once. A
/// second, independent screening is run for (a) every source the first screening included, (b) every first decision
/// with low or medium confidence, or none given, or made without a summary, (c) every source whose text looked like
/// instructions, and (d) a random audit sample of each pool. The sample is drawn from every source the first screening
/// decided, whatever it decided, so Cohen's κ on it estimates the model's stability over the whole pool: a sample of
/// excluded sources alone would give the first screening one answer only, and κ would be 0 or undefined by construction.
/// The sources it adds beyond (a)–(c) are a share of the first screening's other exclusions, which is the saving.
/// </summary>
public static class MultivocalSecondScreening
{
    /// <summary>The share of each pool in the audit sample, rounded up.</summary>
    public const double AuditShare = 0.20;

    /// <summary>The smallest audit sample of a pool; a smaller pool is screened twice whole.</summary>
    public const int AuditMinimum = 10;

    /// <summary>Why a source was screened a second time, besides the audit sample, in the order the rule checks them.</summary>
    public const string Included = "included", Uncertain = "uncertain", Flagged = "flagged";

    /// <summary>
    /// The reason a source gets a targeted second screening, or null when one screening is enough: it was included,
    /// the decision was unsure (low or medium confidence, none given, or no summary to judge by), or its text holds a
    /// strong flag, a phrase aimed at the model (<see cref="PromptSafety.AnyStrong(IEnumerable{string})"/>; a weak mention is not enough, #472).
    /// </summary>
    public static string? TargetReason(ScreeningAnswer first, bool hasSummary, bool flagged) =>
        first.IsIncluded ? Included
        : !hasSummary || first.Confidence is not "high" ? Uncertain
        : flagged ? Flagged
        : null;

    /// <summary>The seed of a run's audit sample: the first eight characters of its id, so anyone can draw it again.</summary>
    public static string SeedOf(Guid runId) => runId.ToString("N")[..8];

    /// <summary>How many of a pool's screened sources are in its audit sample: 20%, rounded up, and at least 10.</summary>
    public static int AuditSize(int screened) => Math.Min(screened, Math.Max(AuditMinimum, (screened + 4) / 5));

    /// <summary>
    /// The audit sample of one pool: its sources ranked by the SHA-256 of the seed and their address, lowest first,
    /// and the first <see cref="AuditSize"/> of them. The rank does not depend on any decision, and any tool can repeat it.
    /// </summary>
    public static IReadOnlyList<string> AuditSample(string seed, IEnumerable<string> addresses)
    {
        var list = addresses.Distinct(StringComparer.Ordinal).ToList();
        return list.OrderBy(a => Rank(seed, a), StringComparer.Ordinal).ThenBy(a => a, StringComparer.Ordinal)
            .Take(AuditSize(list.Count)).ToList();
    }

    /// <summary>A source's place in the draw: the SHA-256 of "seed|address", in lower-case hex.</summary>
    public static string Rank(string seed, string address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{address}"))).ToLowerInvariant();

    /// <summary>Cohen's κ between the two screenings of the given sources, or null when none was screened twice.</summary>
    public static double? Kappa(IEnumerable<GreyScreening> sources)
    {
        var pairs = sources.Where(s => s.First != null && s.Second != null).Select(s => (s.First!.IsIncluded, s.Second!.IsIncluded)).ToList();
        return pairs.Count == 0 ? null : Math.Round(ScreeningConfusion.From(pairs).CohensKappa, 3);
    }

    /// <summary>
    /// What the second screenings of one pool (or of both, when <paramref name="formal"/> is null) did, counted from the
    /// screening file: the method, the flow diagram, the funnel and the paper all take their numbers from here.
    /// </summary>
    public static SecondScreeningCounts Count(MultivocalScreeningFile file, bool? formal)
    {
        var screened = file.Sources.Where(s => s.Decision is "Included" or "Excluded" && (formal == null || s.Formal == formal)).ToList();
        var twice = screened.Where(s => s.Second != null && s.First != null).ToList();
        var targeted = twice.Where(s => s.SecondReason != null).ToList();
        var audit = twice.Where(s => s.AuditSample).ToList();
        double? kappa = formal == null ? file.Kappa : formal.Value ? file.KappaFormal : file.KappaGrey ?? file.Kappa;
        return new SecondScreeningCounts(
            Targeted: file.AuditSeed != null,
            Screened: screened.Count,
            ScreenedTwice: twice.Count,
            TargetedTotal: targeted.Count,
            TargetedAgreed: targeted.Count(s => s.First!.IsIncluded == s.Second!.IsIncluded),
            ForIncluded: targeted.Count(s => s.SecondReason == Included),
            ForUncertain: targeted.Count(s => s.SecondReason == Uncertain),
            ForFlagged: targeted.Count(s => s.SecondReason == Flagged),
            Audit: audit.Count,
            AuditOnly: audit.Count(s => s.SecondReason == null),
            Kappa: kappa);
    }
}

/// <summary>
/// The second screenings of a pool or of a run. <see cref="Targeted"/> is false for a run screened before the rule,
/// when every source was screened twice and κ is over all of them.
/// </summary>
/// <param name="ScreenedTwice">Sources with both screenings: the targeted ones and the audit sample together.</param>
/// <param name="TargetedTotal">Sources screened again for a reason (a)–(c); their agreement is counted, never put into κ.</param>
/// <param name="Audit">Sources in the random audit sample; κ is computed over these only.</param>
/// <param name="AuditOnly">Sources in the audit sample with no other reason for a second screening: what the sample costs.</param>
public sealed record SecondScreeningCounts(bool Targeted, int Screened, int ScreenedTwice, int TargetedTotal, int TargetedAgreed,
    int ForIncluded, int ForUncertain, int ForFlagged, int Audit, int AuditOnly, double? Kappa)
{
    /// <summary>Every screened source was screened twice: a run from before the rule, or a pool no larger than the audit minimum.</summary>
    public bool AllTwice => ScreenedTwice == Screened;

    /// <summary>Sources screened once only.</summary>
    public int Once => Screened - ScreenedTwice;
}

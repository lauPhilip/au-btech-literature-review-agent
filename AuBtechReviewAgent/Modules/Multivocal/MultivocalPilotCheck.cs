using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// A pilot check of the criteria during screening: when more than half of the first 30 sources of a pool are included
/// by the first screening, the criteria may be broad, and a warning is written to the screening file and shown on the
/// run view. It never stops the run and asks the model nothing. "First" is the order of the search ledger, not the
/// order the parallel calls happened to finish, so the same run always gives the same warning.
/// </summary>
public static class MultivocalPilotCheck
{
    /// <summary>How many first decisions of a pool the check looks at; a smaller pool is not checked.</summary>
    public const int PilotSize = 30;

    /// <summary>A pilot with more included sources than this share gets a warning.</summary>
    public const double BroadShare = 0.5;

    /// <summary>
    /// The warning for one pool, or null: <paramref name="firstIncluded"/> holds the first screening's decisions of the
    /// pool's screened sources in ledger order (true for included).
    /// </summary>
    public static string? Warning(IEnumerable<bool> firstIncluded, bool formal)
    {
        var pilot = firstIncluded.Take(PilotSize).ToList();
        if (pilot.Count < PilotSize) return null;
        int included = pilot.Count(i => i);
        if (included <= PilotSize * BroadShare) return null;
        string pool = formal ? "academic" : "grey";
        return $"More than half of the first {PilotSize} {pool} sources were included at the first screening ({included} of {PilotSize}). The criteria may be broad.";
    }
}

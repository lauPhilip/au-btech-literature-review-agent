using System;

namespace AuBtechReviewAgent;

/// <summary>
/// How many records a search asks a source for (issue #681). The cap is per source, and records dropped for their year or
/// as duplicates do not use it up, so the request is what the source still needs, not a fixed share of the cap.
/// </summary>
public static class SearchLimits
{
    /// <summary>A round's request: what the source still needs to fill its cap, shared over the strings left (this one included); at least 1.</summary>
    public static int Round(int cap, int kept, int stringsLeft) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, cap - kept) / (double)Math.Max(1, stringsLeft)));
}

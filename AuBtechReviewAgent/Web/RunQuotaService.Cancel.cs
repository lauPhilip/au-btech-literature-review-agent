using System;

namespace AuBtechReviewAgent;

/// <summary>
/// Giving a run back when it ends early. A failed run is always given back. A run its owner cancelled is given back
/// once per address and tier per UTC day (issue #233), so cancelling a run just before it finishes and starting it
/// again cannot turn one free run into many.
/// </summary>
public partial class RunQuotaService
{
    private const string CancelledPrefix = "cancelled:";

    /// <summary>Gives a cancelled run back, unless this address has had a cancelled run back today already.</summary>
    public bool RefundCancelled(QuotaLease lease)
    {
        lock (_gate)
        {
            DateTime now = _utcNow();
            string marker = CancelledPrefix + lease.Bucket;
            if (Count(marker, t => t.Date == now.Date) > 0) return false;
            Remove(lease.Bucket, lease.ReservedAtUtc);
            if (lease.CountsTowardsGlobal) Remove(GlobalFreeBucket, lease.ReservedAtUtc);
            Add(marker, now);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Settles the quota of a run that ended with an exception: a cancelled run (<see cref="RunCancelledException"/>)
    /// goes through <see cref="RefundCancelled"/>, any other is given back. True when the run was given back.
    /// </summary>
    public bool GiveBackAfter(QuotaLease lease, Exception ended)
    {
        if (ended is RunCancelledException) return RefundCancelled(lease);
        Refund(lease);
        return true;
    }
}

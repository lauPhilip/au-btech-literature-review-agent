using System;

namespace AuBtechReviewAgent;

/// <summary>
/// Who may change a run. The edit keys live in <see cref="RunStore"/>, which every kind of review shares; these
/// members pass through to it so existing callers keep working.
/// </summary>
public partial class PrismaReviewEngine
{
    public const string OwnerFile = RunStore.OwnerFile;

    /// <summary>Creates the run folder and its edit key; returns the key, which only the starting browser keeps.</summary>
    public string ClaimRun(Guid runId) => Store.ClaimRun(runId);

    /// <summary>True when the key belongs to the run (see <see cref="RunStore.CanEdit"/>).</summary>
    public bool CanEdit(Guid runId, string? editKey) => Store.CanEdit(runId, editKey);

    /// <summary>The example run from Runs:DemoRunId, when it is set and the run still exists.</summary>
    public Guid? DemoRunId => Store.DemoRunId;

    public bool IsDemoRun(Guid runId) => Store.IsDemoRun(runId);

    /// <summary>True when the run has an owner record, i.e. it can only be changed with its edit key.</summary>
    public bool HasOwner(Guid runId) => Store.HasOwner(runId);
}

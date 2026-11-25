using Microsoft.AspNetCore.Components;

namespace AuBtechReviewAgent.Components.Pages;

// The report drawn from a gallery entry's copy of the run (Runs/GalleryStore.cs), and the owner's offer to add it.
// The gallery page embeds this page with GalleryId; the report then reads the copy, never the run folder, and nothing
// on it can be changed: no edit key is asked for, so every button that needs one stays away.
public partial class SpecMatrix
{
    [Inject] internal AuBtechReviewAgent.GalleryStore Gallery { get; set; } = default!;

    /// <summary>Set by the gallery page: draw this entry's copy instead of a run.</summary>
    [Parameter] public string? GalleryId { get; set; }

    /// <summary>The gallery entry being shown, or null on a run's own report page.</summary>
    internal AuBtechReviewAgent.GalleryEntry? galleryEntry;

    internal bool InGallery => GalleryId != null;

    /// <summary>The run the report names: the gallery entry's run, or the run this page was opened for.</summary>
    internal Guid ReportRunId => galleryEntry?.RunId ?? State.CurrentSessionId;

    /// <summary>The folder the report is read from: the entry's copy in the gallery, or the run's own folder.</summary>
    internal string ReportFolder() =>
        galleryEntry is { } entry ? Gallery.FolderOf(entry.Id) : Runs.FolderOf(State.CurrentSessionId);

    /// <summary>The run's ledger, from the same folder as the report.</summary>
    internal AuBtechReviewAgent.ReviewState? LoadLedger() =>
        galleryEntry != null ? AuBtechReviewAgent.GallerySnapshot.LoadLedger(ReportFolder()) : ReviewEngine.LoadState(State.CurrentSessionId);

    /// <summary>The owner's offer: only with the edit key, on a finished run's own page, never in the gallery.</summary>
    internal bool CanOfferToGallery => !InGallery && canEdit && Gallery.Enabled && Runs.HasOwner(State.CurrentSessionId) && !ReviewEngine.IsRunActive(State.CurrentSessionId);
}

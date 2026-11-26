using System;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>The lines a gallery card and an entry's page show, written in code from the entry's stored figures.</summary>
public static class GalleryCard
{
    /// <summary>The name of the card the review was started from ("Systematic Review", "Grey Literature Review").</summary>
    public static string TypeLabel(string card) =>
        ReviewModules.ForCard(card).Cards.FirstOrDefault(c => c.Key.Equals(card, StringComparison.OrdinalIgnoreCase))?.Name ?? "Review";

    public static string Date(DateTime utc) => utc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"7 studies from 120 records · 2 themes · 30 citations checked".</summary>
    public static string Counts(GalleryEntry e)
    {
        var parts = new System.Collections.Generic.List<string>
        {
            $"{e.Included} {(e.Included == 1 ? "study" : "studies")} from {e.Identified} {(e.Identified == 1 ? "record" : "records")}",
        };
        if (e.Themes > 0) parts.Add($"{e.Themes} {(e.Themes == 1 ? "theme" : "themes")}");
        parts.Add($"{e.CitationsChecked} {(e.CitationsChecked == 1 ? "citation" : "citations")} checked");
        return string.Join(" · ", parts);
    }

    /// <summary>"TraceableAI 1.4.0 · run 1a2b3c4d · added 20 November 2026 · 3 versions, newest 24 November 2026".</summary>
    public static string Provenance(GalleryEntry e)
    {
        string line = $"TraceableAI {ToolVersion.Display(e.ToolVersion)} · run {e.ShortRunId} · added {Date(e.AddedUtc)}";
        return e.Versions.Count > 1 && e.UpdatedUtc is DateTime updated ? $"{line} · {e.Versions.Count} versions, newest {Date(updated)}" : line;
    }

    /// <summary>The address of one version: "/gallery/{entry}/v/{n}", which stays the same while newer versions are added.</summary>
    public static string VersionPath(string entryId, int number) => $"/gallery/{entryId}/v/{number}";

    /// <summary>"7 studies · 30 citations checked, 80% supported" for the versions list.</summary>
    public static string VersionCounts(GalleryVersion v)
    {
        string line = $"{v.Included} {(v.Included == 1 ? "study" : "studies")} from {v.Identified} · {v.Citations.Checked} {(v.Citations.Checked == 1 ? "citation" : "citations")} checked";
        return v.Citations.HasVerdicts ? $"{line}, {v.Citations.Percent(v.Citations.Supported)}% supported" : line;
    }

    /// <summary>
    /// "How this was made", in two plain sentences from the stored figures; the report's own methods section has the
    /// full account, and the model card and the About page say how the tool works.
    /// </summary>
    public static string HowMade(GalleryEntry e) =>
        $"This {TypeLabel(e.Card).ToLowerInvariant()} was run with TraceableAI {ToolVersion.Display(e.ToolVersion)} and finished on {Date(e.CompletedUtc)}. " +
        $"It found {e.Identified} {(e.Identified == 1 ? "record" : "records")}, included {e.Included} after screening, and checked each of its " +
        $"{e.CitationsChecked} {(e.CitationsChecked == 1 ? "citation" : "citations")} against the source it cites; the methods section of the report describes every step, and the counts come from the run's own files.";
}

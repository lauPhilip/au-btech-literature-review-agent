using System;
using System.Globalization;
using System.Collections.Generic;
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

    /// <summary>
    /// The gallery's pages for the sitemap (issue #542): the catalogue, and each entry whose page can be opened, at its
    /// own address (its newest version), with the day it last changed. The entries are public: their owners agreed to
    /// share them. Older versions keep their addresses but are left out, so a search engine lists each review once.
    /// </summary>
    public static IEnumerable<(string Path, string ChangeFrequency, string Priority, DateTime? Changed)> SitemapPages(GalleryStore store, ReviewModulesOptions modules)
    {
        if (!store.Enabled) yield break;
        var shown = store.List().Where(e => modules.Shows(e.Card) && e.Module == ReviewModules.Default.Key).ToList();
        yield return ("/gallery", "weekly", "0.6", shown.Count > 0 ? shown.Max(e => e.LastChangedUtc) : null);
        foreach (var e in shown) yield return ($"/gallery/{e.Id}", "monthly", "0.5", e.LastChangedUtc);
    }

    /// <summary>
    /// The description a shared link shows (meta description, Open Graph and Twitter, issue #542): what kind of report it
    /// is, then its first research question, cut at a word to about 200 characters. The page escapes it as text.
    /// </summary>
    public static string ShareDescription(GalleryEntry e)
    {
        string text = $"An example {TypeLabel(e.Card).ToLowerInvariant()} made with TraceableAI, with every citation checked against its source.";
        if (e.Questions.FirstOrDefault(q => !string.IsNullOrWhiteSpace(q)) is string question)
            text += " " + string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int max = 200;
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > 100 ? cut : max - 1)].TrimEnd(',', ';', ':', '.') + "…";
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
    /// "Citations 118 of 121 supported, 2 partly", from the citation check's verdicts; null for an entry added before
    /// the verdicts were recorded, whose card says only how many were checked.
    /// </summary>
    public static string? CitationSupport(CitationShares c)
    {
        if (!c.HasVerdicts || c.Checked <= 0) return null;
        string line = $"Citations {c.Supported} of {c.Checked} supported";
        if (c.Partly > 0) line += $", {c.Partly} partly";
        if (c.NotSupported > 0) line += $", {c.NotSupported} not supported";
        return line;
    }

    /// <summary>"Self-check: 8 checks passed, 2 notes, 1 fault"; null when the run made no self-check.</summary>
    public static string? SelfCheckLine(GallerySelfCheck? s) => s == null ? null : $"Self-check: {s.Summary}";

    /// <summary>
    /// "How this was made", in two plain sentences from the stored figures; the report's own methods section has the
    /// full account, and the model card and the About page say how the tool works.
    /// </summary>
    public static string HowMade(GalleryEntry e) =>
        $"This {TypeLabel(e.Card).ToLowerInvariant()} was run with TraceableAI {ToolVersion.Display(e.ToolVersion)} and finished on {Date(e.CompletedUtc)}. " +
        $"It found {e.Identified} {(e.Identified == 1 ? "record" : "records")}, included {e.Included} after screening, and checked each of its " +
        $"{e.CitationsChecked} {(e.CitationsChecked == 1 ? "citation" : "citations")} against the source it cites; the methods section of the report describes every step, and the counts come from the run's own files.";
}

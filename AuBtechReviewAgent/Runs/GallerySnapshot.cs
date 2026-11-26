using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// What the gallery copies from a finished run, and what its catalogue card says about it. The copy holds the run's
/// report files, the same files at the top of its archive that main.tex is written from, so the report can be drawn
/// again, with the print layout of the day, after the run is gone. The reviewer's notes (reviewer-notes.json), the
/// edit key's record (owner.json) and the model call log (llm-calls.json, which holds every prompt with the papers'
/// text) are never copied, and nor are the downloaded papers or the raw database answers, which are in sub-folders.
/// The gallery is public, so the copy follows the public-copy rules (<see cref="PublicCopy"/>): no abstracts from
/// licensed databases and short quotes, with public-copy.json saying what was shortened.
/// </summary>
public static class GallerySnapshot
{
    public const string LedgerFile = "transparent-process.json";
    public const string ReportFile = "prisma-report.json";
    public const string AuditFile = "citation-audit.json";

    /// <summary>The largest copy the gallery takes of one run, so one entry cannot fill the disk.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>Files that must never be copied, whatever a module lists.</summary>
    public static IReadOnlyList<string> NeverCopied { get; } = new[] { RunStore.OwnerFile, RunStore.NotesFile, "llm-calls.json" };

    /// <summary>
    /// The systematic review's files at the top of its archive, without those never copied: what the report page reads
    /// (Components/Pages/SpecMatrix*) and what a print render written from the run's files, like main.tex, needs.
    /// </summary>
    public static IReadOnlyList<string> SystematicFiles { get; } = ReviewModules.Default.ArchiveFiles
        .Where(f => !NeverCopied.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The files to copy for a run of this module; empty for a module the gallery does not take yet. A multivocal paper
    /// cites web pages whose text is kept only for the run's lifetime (privacy page), so it needs a copy without that
    /// text before it can be offered (#508).
    /// </summary>
    public static IReadOnlyList<string> FilesFor(string module) =>
        (module == ReviewModules.Default.Key ? SystematicFiles : Array.Empty<string>())
            .Where(f => !NeverCopied.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The catalogue card of a finished systematic run, from its ledger and report; null when the run has not finished
    /// with a report (a failed, cancelled or running review is never offered).
    /// </summary>
    public static GalleryEntry? Describe(string folder, RunHeader header)
    {
        if (header.Module != ReviewModules.Default.Key) return null;
        var state = ReadJson<ReviewState>(Path.Join(folder, LedgerFile));
        var report = ReadJson<PrismaReport>(Path.Join(folder, ReportFile));
        if (state?.CompletedUtc is not DateTime completed || state.CancelledUtc != null || state.FailureMessage != null) return null;
        if (report == null || string.IsNullOrWhiteSpace(report.TitleItem) || report.TitleItem == "Pending...") return null;

        var questions = (state.ResearchQuestions ?? new()).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList();
        string topic = string.IsNullOrWhiteSpace(state.ReviewObjective) ? state.SearchQuery : state.ReviewObjective;
        var keywords = (state.ThematicSynthesis?.Themes ?? new())
            .Select(t => t.Name.Trim()).Where(n => n.Length is > 0 and <= 80).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        return new GalleryEntry
        {
            RunId = header.RunId,
            Module = header.Module,
            Card = string.IsNullOrWhiteSpace(header.Card) ? ReviewMethod.SystematicKey : header.Card,
            Title = report.TitleItem.Trim(),
            Questions = questions,
            CompletedUtc = completed,
            ToolVersion = state.RunSettings?.AppVersion ?? "",
            Included = state.Stats.Included,
            Identified = state.Stats.TotalIdentified,
            Themes = state.Stats.ThemesIdentified,
            CitationsChecked = state.Stats.CitationsChecked,
            Citations = CitationShares.Of(state.Stats),
            References = state.SynthesizedRecords?.Count ?? 0,
            Keywords = keywords,
            Fingerprint = GalleryFingerprint.Of(string.IsNullOrWhiteSpace(header.Card) ? ReviewMethod.SystematicKey : header.Card, topic, questions),
        };
    }

    /// <summary>Makes a fresh copy public before it is shown: abstracts and quotes as <see cref="PublicCopy"/> allows.</summary>
    public static PublicCopyRecord MakePublic(string copyFolder) => PublicCopy.ApplyToFolder(copyFolder, GalleryEntry.FileName);

    /// <summary>
    /// Copies taken before the public-copy rules hold every abstract and full quotes; each copy without public-copy.json
    /// is made public once, when the gallery starts. Returns how many copies were changed.
    /// </summary>
    public static int MakeOlderCopiesPublic(string galleryRoot)
    {
        if (!Directory.Exists(galleryRoot)) return 0;
        int changed = 0;
        foreach (var ledger in Directory.EnumerateFiles(galleryRoot, LedgerFile, SearchOption.AllDirectories))
        {
            string folder = Path.GetDirectoryName(ledger)!;
            if (Path.GetFileName(folder).StartsWith('.') || File.Exists(Path.Join(folder, PublicCopy.RecordFile))) continue;
            MakePublic(folder);
            changed++;
        }
        return changed;
    }

    /// <summary>A copied run's ledger, for the report page drawn from the gallery; null when it cannot be read.</summary>
    public static ReviewState? LoadLedger(string folder) => ReadJson<ReviewState>(Path.Join(folder, LedgerFile));

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Read); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}

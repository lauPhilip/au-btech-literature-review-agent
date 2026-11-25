using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// What the gallery copies from a finished run, and what its catalogue card says about it. The copy holds only the
/// files the report page reads, so the report can be drawn again, with the print layout of the day, after the run is
/// gone. The reviewer's notes (reviewer-notes.json) and the edit key's record (owner.json) are never copied, and nor
/// are the downloaded papers, the raw database answers or the model call log.
/// </summary>
public static class GallerySnapshot
{
    public const string LedgerFile = "transparent-process.json";
    public const string ReportFile = "prisma-report.json";
    public const string AuditFile = "citation-audit.json";

    /// <summary>The largest copy the gallery takes of one run, so one entry cannot fill the disk.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>The files the systematic review's report page reads (Components/Pages/SpecMatrix*).</summary>
    public static IReadOnlyList<string> SystematicFiles { get; } = new[]
    {
        RunHeader.FileName, ReportFile, LedgerFile, AuditFile, ReportEditLog.FileName, RunStore.ProtocolFile,
    };

    /// <summary>Files that must never be copied, whatever a module lists.</summary>
    public static IReadOnlyList<string> NeverCopied { get; } = new[] { RunStore.OwnerFile, RunStore.NotesFile };

    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The files to copy for a run of this module; empty for a module the gallery does not take yet.</summary>
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
            Keywords = keywords,
            Fingerprint = GalleryFingerprint.Of(string.IsNullOrWhiteSpace(header.Card) ? ReviewMethod.SystematicKey : header.Card, topic, questions),
        };
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

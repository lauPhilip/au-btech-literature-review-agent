using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Writes a systematic run's archive from its folder, and builds its main.tex alone. The archive goes to a stream,
/// a file for a download, one file at a time (<see cref="RunArchive.Write"/>), so the source PDFs are never all in
/// memory at once.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// Loads the persisted report and synthesized records of a run straight off disk and builds the archive in
    /// memory. Kept for tests and the MCP server; a download uses <see cref="WriteWorkspaceArchive"/>.
    /// </summary>
    public byte[] GenerateWorkspaceArchiveFromDisk(Guid sessionId)
    {
        using var memory = new MemoryStream();
        return WriteWorkspaceArchive(sessionId, memory) ? memory.ToArray() : Array.Empty<byte>();
    }

    /// <summary>
    /// Writes the archive of a run to <paramref name="output"/>. Served from a plain HTTP endpoint instead of the
    /// Blazor connection, whose small message size fails once real source PDFs are included. False when the run
    /// has no systematic review state; nothing is written then.
    /// </summary>
    public bool WriteWorkspaceArchive(Guid sessionId, Stream output)
    {
        var state = LoadState(sessionId);
        if (state == null) return false;
        var generated = GeneratedFiles(LoadArchiveReport(sessionId), state.SynthesizedRecords, state);
        var module = ReviewModules.Find(ReviewMethod.SystematicKey)!;
        RunArchive.Write(output, sessionId, GetWorkspaceFolderPath(sessionId), generated, module.ArchiveFiles,
            state.ProtocolHash, state.RunSettings?.Model);
        return true;
    }

    /// <summary>
    /// The main.tex the archive holds, built on its own: the source texts are not read, and no zip is made.
    /// Null when the run has no systematic review state.
    /// </summary>
    public string? MainTexFromDisk(Guid sessionId)
    {
        var state = LoadState(sessionId);
        if (state == null) return null;
        var mainTex = GeneratedFiles(LoadArchiveReport(sessionId), state.SynthesizedRecords, state).First(f => f.Path == "main.tex");
        return new UTF8Encoding(false).GetString(mainTex.Content);
    }

    // NOTE: historically named GenerateManuscriptPdf, but it never compiled a PDF - it always returned a
    // zip bundle (LaTeX source + JSON ledger + source PDFs). Renamed to match what it actually produces.
    public byte[] GenerateWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state = null)
    {
        var generated = GeneratedFiles(report, records, state);
        var module = ReviewModules.Find(ReviewMethod.SystematicKey)!;
        return RunArchive.Build(sessionId, GetWorkspaceFolderPath(sessionId), generated, module.ArchiveFiles,
            state?.ProtocolHash, state?.RunSettings?.Model);
    }

    private List<(string Path, byte[] Content)> GeneratedFiles(PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state)
    {
        // The LaTeX interpolates decimals (pie-slice angles, axis limits). On a machine with a Danish (or any
        // comma-decimal) culture those came out as "51,43", which breaks the TikZ syntax, so the document is
        // always written with the invariant culture.
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { return BuildGeneratedFiles(report, records, state); }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    /// <summary>The run's saved report with the reviewer's checked edits applied, as main.tex shows it.</summary>
    private PrismaReport LoadArchiveReport(Guid sessionId)
    {
        var report = new PrismaReport();
        string reportPath = GetReportFilePath(sessionId);
        if (File.Exists(reportPath))
        {
            try
            {
                string content = File.ReadAllText(reportPath);
                report = JsonSerializer.Deserialize<PrismaReport>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrismaReport();
            }
            catch (Exception ex)
            {
                // The archive is still built from the ledger; only the report section is missing.
                _log.LogWarning("Archive: could not read prisma-report.json: {Message}", SanitizeLogMessage(ex.Message));
            }
        }

        // The reviewer's checked edits (report-edits.json) go into main.tex; a run without edits is written as before.
        return ReportEdits.Apply(report, LoadReportEdits(sessionId));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// The downloadable archive of a run, for every kind of review. The core part is the same for all of them: the
/// files every run folder holds (its header, protocol, model calls, notes and metrics), what each source returned
/// exactly as received, the downloaded source texts, and manifest.json with the SHA-256 of every file. A module
/// adds what only it has: its ledger and audit files (<see cref="IReviewModule.ArchiveFiles"/>) and the files it
/// generates for the download, such as a LaTeX report or a bibliography.
/// </summary>
public static class RunArchive
{
    /// <summary>Files every run may hold, whatever its module; always kept at the archive root when present.</summary>
    public static readonly IReadOnlyList<string> CoreFiles = new[]
    {
        RunHeader.FileName, RunStore.ProtocolFile, "llm-calls.json", RunStore.NotesFile, "run-metrics.json",
    };

    /// <summary>Folder of each source's answers exactly as received.</summary>
    public const string RawResponsesFolder = "SourceResponses";

    /// <summary>Folder in the archive for the source texts downloaded during the run.</summary>
    public const string SourceTextsFolder = "SourcePapers";

    private static ILogger _log => AppLog.For("AuBtechReviewAgent.RunArchive");

    /// <summary>
    /// Builds the zip. <paramref name="generated"/> come first, then the root files in the order the module lists
    /// them (with any core file it did not list added after them), then the downloaded source texts and the raw
    /// source answers, and last manifest.json, which fingerprints everything before it. Other .json files in the
    /// run folder (such as owner.json with the edit key's hash) and temporary files are never included.
    /// </summary>
    public static byte[] Build(
        Guid runId,
        string runFolder,
        IReadOnlyList<(string Path, byte[] Content)> generated,
        IReadOnlyList<string> moduleFiles,
        string? protocolHash,
        string? model)
    {
        var entries = new List<(string Path, byte[] Content)>(generated);
        void AddFile(string path, string name)
        {
            var bytes = ReadShared(path);
            if (bytes != null) entries.Add((name, bytes));
        }

        var rootFiles = moduleFiles.Concat(CoreFiles.Where(c => !moduleFiles.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var name in rootFiles)
        {
            string path = Path.Join(runFolder, name);
            if (File.Exists(path)) AddFile(path, name);
        }

        if (Directory.Exists(runFolder))
        {
            // Downloaded source texts.
            foreach (var file in Directory.GetFiles(runFolder).OrderBy(f => f, StringComparer.Ordinal))
            {
                string filename = Path.GetFileName(file);
                if (rootFiles.Contains(filename, StringComparer.OrdinalIgnoreCase)) continue;
                if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                AddFile(file, $"{SourceTextsFolder}/{filename}");
            }
            // What each source returned, exactly as received.
            string rawFolder = Path.Join(runFolder, RawResponsesFolder);
            if (Directory.Exists(rawFolder))
                foreach (var file in Directory.GetFiles(rawFolder).OrderBy(f => f, StringComparer.Ordinal))
                    AddFile(file, $"{RawResponsesFolder}/{Path.GetFileName(file)}");
        }

        entries.Add(("manifest.json", new UTF8Encoding(false).GetBytes(RunManifest.Build(runId, protocolHash, model, entries))));

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var target = entry.Open();
                target.Write(content, 0, content.Length);
            }
        }
        return memoryStream.ToArray();
    }

    // Reads with FileShare.ReadWrite so a download never fails because the run is writing its ledger.
    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            return copy.ToArray();
        }
        catch (IOException ex)
        {
            _log.LogWarning("Archive: skipped {File}: {Message}", Sanitize(Path.GetFileName(path)), Sanitize(ex.Message));
            return null;
        }
    }

    // Line breaks are removed so a value cannot add fake lines to the log, and anything that looks like a key is hidden.
    private static string Sanitize(string message) =>
        Regex.Replace(message.Replace("\r", " ").Replace("\n", " "), @"([a-zA-Z0-9]{24,64})", "[REDACTED_API_CREDENTIAL]");
}

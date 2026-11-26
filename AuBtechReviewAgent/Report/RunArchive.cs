using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
        RunHeader.FileName, RunStore.ProtocolFile, "llm-calls.json", RunStore.NotesFile, "run-metrics.json", ReportEditLog.FileName,
    };

    /// <summary>Folder of each source's answers exactly as received.</summary>
    public const string RawResponsesFolder = "SourceResponses";

    /// <summary>Folder in the archive for the source texts downloaded during the run.</summary>
    public const string SourceTextsFolder = "SourcePapers";

    private static ILogger _log => AppLog.For("AuBtechReviewAgent.RunArchive");

    /// <summary>
    /// Builds the zip in memory. Kept for tests and small callers; a download writes to a file with
    /// <see cref="Write"/>, which never holds more than one buffer of a file at a time.
    /// </summary>
    public static byte[] Build(
        Guid runId,
        string runFolder,
        IReadOnlyList<(string Path, byte[] Content)> generated,
        IReadOnlyList<string> moduleFiles,
        string? protocolHash,
        string? model)
    {
        using var memoryStream = new MemoryStream();
        Write(memoryStream, runId, runFolder, generated, moduleFiles, protocolHash, model);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Writes the zip to <paramref name="output"/>. <paramref name="generated"/> come first, then the root files in
    /// the order the module lists them (with any core file it did not list added after them), then the downloaded
    /// source texts and the raw source answers, and last manifest.json, which fingerprints everything before it.
    /// Other .json files in the run folder (such as owner.json with the edit key's hash) and temporary files are
    /// never included. Each file is copied from disk into the zip in pieces and fingerprinted as it goes, so the
    /// manifest holds the SHA-256 of exactly the bytes written, and a 40 MB source PDF never sits in memory.
    /// </summary>
    public static void Write(
        Stream output,
        Guid runId,
        string runFolder,
        IReadOnlyList<(string Path, byte[] Content)> generated,
        IReadOnlyList<string> moduleFiles,
        string? protocolHash,
        string? model)
    {
        var fingerprints = new List<RunManifest.Entry>();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in generated)
            {
                using var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                target.Write(content, 0, content.Length);
                fingerprints.Add(new RunManifest.Entry(name, content.LongLength, RunManifest.Sha256(content)));
            }

            foreach (var (path, name) in DiskFiles(runFolder, moduleFiles))
                if (CopyShared(archive, path, name) is { } entry) fingerprints.Add(entry);

            byte[] manifest = new UTF8Encoding(false).GetBytes(RunManifest.Build(runId, protocolHash, model, fingerprints));
            using var manifestTarget = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            manifestTarget.Write(manifest, 0, manifest.Length);
        }
    }

    /// <summary>The run folder's files that go in the archive, in archive order, with their names in it.</summary>
    private static IEnumerable<(string Path, string Name)> DiskFiles(string runFolder, IReadOnlyList<string> moduleFiles)
    {
        var rootFiles = moduleFiles.Concat(CoreFiles.Where(c => !moduleFiles.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var name in rootFiles)
        {
            string path = Path.Join(runFolder, name);
            if (File.Exists(path)) yield return (path, name);
        }

        if (!Directory.Exists(runFolder)) yield break;
        // Downloaded source texts.
        foreach (var file in Directory.GetFiles(runFolder).OrderBy(f => f, StringComparer.Ordinal))
        {
            string filename = Path.GetFileName(file);
            if (rootFiles.Contains(filename, StringComparer.OrdinalIgnoreCase)) continue;
            // A PDF's record (FullTextFile) says which paper the file is, so it goes in beside it; other JSON is the run's own.
            if ((filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !FullTextFile.IsRecordFile(filename))
                || filename.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (file, $"{SourceTextsFolder}/{filename}");
        }
        // What each source returned, exactly as received.
        string rawFolder = Path.Join(runFolder, RawResponsesFolder);
        if (Directory.Exists(rawFolder))
            foreach (var file in Directory.GetFiles(rawFolder).OrderBy(f => f, StringComparer.Ordinal))
                yield return (file, $"{RawResponsesFolder}/{Path.GetFileName(file)}");
    }

    // Reads with FileShare.ReadWrite so a download never fails because the run is writing its ledger. A file that
    // cannot be opened is left out, as before. One that fails part way stays in with the bytes that were written,
    // and the manifest fingerprints those, so the archive still checks out.
    private static RunManifest.Entry? CopyShared(ZipArchive archive, string path, string name)
    {
        FileStream source;
        try { source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920); }
        catch (IOException ex)
        {
            _log.LogWarning("Archive: skipped {File}: {Message}", Sanitize(Path.GetFileName(path)), Sanitize(ex.Message));
            return null;
        }
        using (source)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            var buffer = new byte[81920];
            long length = 0;
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
            }
            catch (IOException ex)
            {
                _log.LogWarning("Archive: {File} was cut short: {Message}", Sanitize(Path.GetFileName(path)), Sanitize(ex.Message));
            }
            return new RunManifest.Entry(name, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
    }

    // Line breaks are removed so a value cannot add fake lines to the log, and anything that looks like a key is hidden.
    private static string Sanitize(string message) =>
        Regex.Replace(message.Replace("\r", " ").Replace("\n", " "), @"([a-zA-Z0-9]{24,64})", "[REDACTED_API_CREDENTIAL]");
}

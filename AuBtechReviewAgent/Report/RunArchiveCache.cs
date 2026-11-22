using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Keeps the archive of a finished run in its folder, so a repeat download does not build the zip again (issue
/// #306). The kept zip is exactly what <see cref="RunArchive.Build"/> made, named after the folder's
/// <see cref="RunFolderStamp"/>: a note, a re-check or any other change to the folder gives a new stamp, and the
/// next download builds a fresh archive and replaces the old one. The name ends in .tmp, which the archive, the
/// stamp and the clean-up's age of a run all leave out, and the kept zip goes when the run folder is deleted.
/// A run that is still going, or one stopped part way, is never kept.
/// </summary>
public static class RunArchiveCache
{
    public const string Prefix = "archive-cache-";
    public const string Suffix = ".zip.tmp";

    private static ILogger _log => AppLog.For("AuBtechReviewAgent.RunArchiveCache");

    public static string FileName(RunFolderStamp stamp) => Prefix + stamp.Key + Suffix;

    /// <summary>A kept archive, or one being written; the clean-up does not count it as activity.</summary>
    public static bool IsCacheFile(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) && fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a run's archive may be kept: it has a header, is not running and has ended (a checked multivocal run).</summary>
    public static bool IsFinished(RunHeader? header, bool active) =>
        header != null && !active &&
        (header.Module == "multivocal" ? header.Stage == MultivocalWriter.StageChecked : PrismaReviewEngine.IsFinishedStage(header.Stage));

    /// <summary>
    /// The kept archive when its stamp matches, else the one <paramref name="build"/> makes, kept for next time
    /// when the run is finished and nothing in the folder changed while it was built.
    /// </summary>
    public static Stream GetOrBuild(string folder, RunFolderStamp stamp, bool finished, Func<byte[]> build)
    {
        string path = Path.Join(folder, FileName(stamp));
        if (finished)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (FileNotFoundException) { /* not kept yet */ }
            catch (IOException ex) { _log.LogWarning("Archive cache: could not read the kept archive: {Message}", ex.GetType().Name); }
        }

        byte[] zip = build();
        if (finished && zip.Length > 0 && RunFolderStamp.Of(folder)?.ETag == stamp.ETag) Keep(folder, path, zip);
        return new MemoryStream(zip, writable: false);
    }

    private static void Keep(string folder, string path, byte[] zip)
    {
        string part = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(part, zip);
            File.Move(part, path, overwrite: true);
            foreach (var old in Directory.EnumerateFiles(folder, Prefix + "*" + Suffix).Where(f => !string.Equals(Path.GetFileName(f), Path.GetFileName(path), StringComparison.Ordinal)))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keeping the archive only saves time; the download goes on without it.
            _log.LogWarning("Archive cache: could not keep the archive: {Message}", ex.GetType().Name);
            try { File.Delete(part); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("Archive cache: a part file stays until the run folder goes: {Message}", cleanup.GetType().Name);
            }
        }
    }
}

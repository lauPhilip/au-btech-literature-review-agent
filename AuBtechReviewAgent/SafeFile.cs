using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Writes a file by writing a temporary copy and moving it into place, so a reader never sees half a file.
/// On Windows the move can fail for a moment with "Access to the path is denied" when another program has
/// the target open without allowing deletion, which antivirus scanners and the search indexer do for a
/// fraction of a second after every write. A run that saves its ledger many times a second (screening
/// from the cache) hit exactly that and stopped. So the move is retried a few times; if it still fails, the
/// file is overwritten in place, and only if that fails too is the error passed on.
/// </summary>
public static class SafeFile
{
    /// <summary>
    /// Removes a leftover temporary copy. If even that is locked, the copy stays behind: it is ignored by
    /// everything that reads the run folder and goes with the folder when the run is deleted, so it is logged
    /// instead of failing a save that has already succeeded.
    /// </summary>
    private static void DeleteQuietly(string tmp)
    {
        try { File.Delete(tmp); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.For(nameof(SafeFile)).LogDebug("Temporary file {File} could not be removed: {Message}", Path.GetFileName(tmp), ex.Message);
        }
    }

    public static async Task WriteAllTextAsync(string path, string content, int attempts = 6, CancellationToken cancellationToken = default)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tmp, content, cancellationToken);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < attempts)
                {
                    await Task.Delay(25 * attempt * attempt, cancellationToken); // 25, 100, 225, 400, 625 ms
                    continue;
                }
                try
                {
                    // Last resort: overwrite in place (readers share the file, so this usually succeeds when
                    // replacing it does not), then remove the temporary copy.
                    await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    await using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(content);
                    return;
                }
                finally
                {
                    DeleteQuietly(tmp);
                }
            }
        }
    }
}

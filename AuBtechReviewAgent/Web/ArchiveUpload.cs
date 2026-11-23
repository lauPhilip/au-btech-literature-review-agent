using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The upload on /verify, made safe for a small server. One archive check may unpack up to
/// <see cref="RunManifest.MaxUnpackedBytes"/> in memory, so only one runs at a time across the whole site, and a second
/// visitor is asked to try again in a minute. The upload itself goes to a temporary file that is deleted when it is
/// closed, not to memory, and it must be a .zip that starts like one: anything else is refused before it is read.
/// </summary>
public static class ArchiveUpload
{
    public const int MaxMb = 200;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Why the file is refused before it is read; null when its name and size are fine.</summary>
    public static string? Refuse(string fileName, long size)
    {
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return "Only a .zip run archive can be checked.";
        if (size > MaxMb * 1024L * 1024L) return $"The file is larger than {MaxMb} MB.";
        if (size < 22) return "The file is too small to be a zip archive."; // a zip's end record alone is 22 bytes
        return null;
    }

    /// <summary>
    /// Copies the upload to a temporary file and checks it, or returns null when another check is running. Throws
    /// <see cref="InvalidDataException"/> when the file does not start like a zip archive.
    /// </summary>
    public static async Task<RunManifest.ArchiveCheck?> CheckAsync(Stream upload, CancellationToken cancellationToken = default)
    {
        if (!await OneAtATime.WaitAsync(0, cancellationToken)) return null;
        try
        {
            string path = Path.Join(Path.GetTempPath(), "verify-" + Guid.NewGuid().ToString("N") + ".zip.tmp");
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await upload.CopyToAsync(file, cancellationToken);
            file.Position = 0;
            var head = new byte[4];
            if (await file.ReadAsync(head, cancellationToken) < 4 || head[0] != (byte)'P' || head[1] != (byte)'K' || head[2] != 3 || head[3] != 4)
                throw new InvalidDataException("The file is not a valid zip archive.");
            file.Position = 0;
            return await Task.Run(() => RunManifest.CheckArchive(file), cancellationToken);
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}

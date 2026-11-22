using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// What a run folder holds right now, as one value (issue #306): the newest write time of its files, for
/// Last-Modified, and a fingerprint of every file's path, size and write time with the app version, for an ETag
/// and as the key of the kept archive. A new note, a re-check, a deleted file or a new version of the app gives a
/// new fingerprint, so a download built from the folder is never served stale. Half-written files (.tmp), which
/// include the kept archive itself, are left out.
/// </summary>
public sealed record RunFolderStamp(DateTimeOffset LastModified, string ETag)
{
    /// <summary>The stamp of the folder, or null when it does not exist.</summary>
    public static RunFolderStamp? Of(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !IsLeftOut(f.Name))
            .Select(f => (Path: Path.GetRelativePath(folder, f.FullName), f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var text = new StringBuilder(ToolVersion.Current).Append('\n');
        foreach (var (path, length, written) in files)
            text.Append(path).Append('|').Append(length).Append('|').Append(written.Ticks).Append('\n');
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32].ToLowerInvariant();

        DateTime newest = files.Count == 0 ? Directory.GetLastWriteTimeUtc(folder) : files.Max(f => f.LastWriteTimeUtc);
        // HTTP dates have whole seconds; a later write in the same second still changes the ETag.
        var lastModified = new DateTimeOffset(newest.Ticks - newest.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return new RunFolderStamp(lastModified, $"\"{hash}\"");
    }

    /// <summary>The fingerprint without its quotes, for a file name.</summary>
    public string Key => ETag.Trim('"');

    /// <summary>Files that are not part of what the folder holds: half-written files and the kept archive.</summary>
    public static bool IsLeftOut(string fileName) => fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
}

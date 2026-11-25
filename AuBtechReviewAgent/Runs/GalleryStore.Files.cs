using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

// The gallery's folders: writing a version's copy and entry.json, removing versions, the cap, and moving an entry
// written before versions into v/1. Every write goes to a temporary name first and is moved into place, so a reader
// never sees half a version or half an entry.json.
public sealed partial class GalleryStore
{
    private void CopyVersion(string entryId, int number, string source, IReadOnlyList<string> files)
    {
        string target = VersionFolderOf(entryId, number);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = Path.Join(Root, $".new-{entryId}-{number}");
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        foreach (var file in files)
        {
            string from = Path.Join(source, Path.GetFileName(file));
            if (File.Exists(from)) File.Copy(from, Path.Join(temp, Path.GetFileName(file)));
        }
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(temp, target);
    }

    private void WriteEntryFile(GalleryEntry entry)
    {
        string folder = FolderOf(entry.Id);
        Directory.CreateDirectory(folder);
        string path = Path.Join(folder, GalleryEntry.FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entry, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Deletes these versions; the entry then shows its newest remaining version, or goes when none is left.</summary>
    private void RemoveVersions(GalleryEntry entry, IReadOnlyCollection<GalleryVersion> remove)
    {
        var numbers = remove.Select(v => v.Number).ToHashSet();
        var left = entry.Versions.Where(v => !numbers.Contains(v.Number)).OrderBy(v => v.Number).ToList();
        if (left.Count == 0)
        {
            Directory.Delete(FolderOf(entry.Id), recursive: true);
            return;
        }
        var newest = left[^1];
        WriteEntryFile(entry.ForVersion(newest) with
        {
            Versions = left,
            AddedUtc = left[0].AddedUtc,
            UpdatedUtc = left.Count > 1 ? newest.AddedUtc : null,
        });
        foreach (int number in numbers)
        {
            string folder = VersionFolderOf(entry.Id, number);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Keeps the gallery at MaxEntries versions. History goes before any review leaves: the oldest version of the review
    /// with the most versions is dropped first (the one changed longest ago when two have as many), so one review run
    /// many times cannot push the others out. Only when no review has two versions does the review changed longest ago
    /// go, never the one just added.
    /// </summary>
    private void DropOverCap(string keep)
    {
        while (true)
        {
            var all = AllStored();
            if (all.Sum(e => e.Versions.Count) <= MaxEntries) return;
            var largest = all.Where(e => e.Versions.Count > 1)
                .OrderByDescending(e => e.Versions.Count).ThenBy(e => e.LastChangedUtc).FirstOrDefault();
            if (largest != null)
            {
                var oldest = largest.Versions.MinBy(v => v.Number)!;
                RemoveVersions(largest, new[] { oldest });
                _log.LogInformation("Gallery entry {Entry} version {Version} dropped: the gallery keeps {Max} versions.", largest.Id, oldest.Number, MaxEntries);
                continue;
            }
            var old = all.Where(e => e.Id != keep).OrderBy(e => e.LastChangedUtc).FirstOrDefault();
            if (old == null) return;
            Directory.Delete(FolderOf(old.Id), recursive: true);
            _log.LogInformation("Gallery entry {Entry} dropped: the gallery keeps {Max} versions.", old.Id, MaxEntries);
        }
    }

    /// <summary>
    /// An entry written before versions kept its files next to entry.json and one owner token. It becomes version 1 of
    /// itself, with the citation verdicts read from its copied ledger, so its address and its owner's right to withdraw
    /// stay as they were.
    /// </summary>
    private void MoveOldEntriesIntoVersions()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var folder in Directory.EnumerateDirectories(Root).Where(d => GalleryFingerprint.IsEntryId(Path.GetFileName(d))))
        {
            try
            {
                if (ReadEntry(folder) is not { Versions.Count: 0 } old) continue;
                string target = VersionFolderOf(old.Id, 1);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetFileName(f) != GalleryEntry.FileName))
                    File.Move(file, Path.Join(target, Path.GetFileName(file)), overwrite: true);
                var stats = GallerySnapshot.LoadLedger(target)?.Stats;
                var described = stats == null ? old : old with { Citations = CitationShares.Of(stats) };
                var v1 = GalleryVersion.From(described, 1, old.AddedUtc, old.OwnerTokenSha256);
                WriteEntryFile(described with { Versions = new() { v1 }, LastVersion = 1, Version = 1, OwnerTokenSha256 = "" });
                _log.LogInformation("Gallery entry {Entry} moved into version 1.", old.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Gallery entry {Folder} not moved into versions: {Message}",
                    PrismaReviewEngine.SanitizeLogMessage(Path.GetFileName(folder)), PrismaReviewEngine.SanitizeLogMessage(ex.Message));
            }
        }
    }

    private List<GalleryEntry> AllStored()
    {
        if (!Directory.Exists(Root)) return new();
        return Directory.EnumerateDirectories(Root)
            .Where(d => GalleryFingerprint.IsEntryId(Path.GetFileName(d)))
            .Select(ReadEntry).OfType<GalleryEntry>().ToList();
    }

    private static GalleryEntry? ReadEntry(string folder)
    {
        string path = Path.Join(folder, GalleryEntry.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<GalleryEntry>(File.ReadAllText(path), Json);
            return entry != null && entry.IsValidId && entry.Id == Path.GetFileName(folder) ? entry : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Gallery entry {Folder} unreadable: {Message}",
                PrismaReviewEngine.SanitizeLogMessage(Path.GetFileName(folder)), PrismaReviewEngine.SanitizeLogMessage(ex.Message));
            return null;
        }
    }
}

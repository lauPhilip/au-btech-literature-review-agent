using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>What happened when an owner asked to add a report to the gallery; Entry is set when it was added or updated.</summary>
public sealed record GalleryResult(bool Ok, string Message, GalleryEntry? Entry = null, bool Updated = false);

/// <summary>
/// The gallery of example reports: finished runs whose owners said yes to publishing them. Each entry is a folder,
/// App_Data/gallery/{entry id}/, holding a copy of the run's report files (GallerySnapshot) and entry.json. It lives
/// outside the run folders, as the metrics store does, so it is not removed by the run clean-up.
///
/// The rules: nothing is added without the owner's explicit consent and edit key; one review has one entry, so a new
/// consent for the same fingerprint replaces the report in place (same address, "updated on"); the gallery keeps at
/// most Gallery:MaxEntries entries and drops the one changed longest ago; whoever holds the run's edit key can withdraw
/// the entry at any time; and an administrator hides an entry with Gallery:HiddenIds.
/// </summary>
public sealed class GalleryStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static ILogger _log => AppLog.For<GalleryStore>();

    private readonly GalleryOptions _options;
    private readonly RunStore _runs;
    private readonly HashSet<string> _hidden;

    public GalleryStore(GalleryOptions options, string contentRoot, RunStore runs)
    {
        _options = options;
        _runs = runs;
        Root = Path.GetFullPath(Path.IsPathRooted(options.Folder) ? options.Folder : Path.Join(contentRoot, options.Folder));
        _hidden = new HashSet<string>(options.HiddenIds.Select(h => h.Trim().ToLowerInvariant()), StringComparer.Ordinal);
    }

    public string Root { get; }
    public bool Enabled => _options.Enabled;
    public int MaxEntries => Math.Max(1, _options.MaxEntries);

    /// <summary>The entry's folder; only for a well-formed id, so it can never resolve outside the gallery.</summary>
    public string FolderOf(string entryId)
    {
        if (!GalleryFingerprint.IsEntryId(entryId)) throw new ArgumentException("Not a gallery entry id.", nameof(entryId));
        return Path.Join(Root, entryId);
    }

    /// <summary>Every entry the site shows, the most recently changed first; hidden ones are left out.</summary>
    public IReadOnlyList<GalleryEntry> List() =>
        !Enabled ? Array.Empty<GalleryEntry>() : AllStored().Where(e => !_hidden.Contains(e.Id)).OrderByDescending(e => e.LastChangedUtc).ToList();

    /// <summary>One entry the site shows, or null (unknown, hidden or badly formed id).</summary>
    public GalleryEntry? Find(string? entryId) =>
        Enabled && GalleryFingerprint.IsEntryId(entryId) && !_hidden.Contains(entryId!) ? ReadEntry(Path.Join(Root, entryId!)) : null;

    /// <summary>The shown entry made from this run, so its report page can say it is in the gallery.</summary>
    public GalleryEntry? ForRun(Guid runId) => List().FirstOrDefault(e => e.RunId == runId);

    /// <summary>
    /// Adds the run's report to the gallery, or replaces the report of the same review. Refused without consent,
    /// without the run's edit key, for a run that is still going, the example run, or a run that did not finish with a report.
    /// </summary>
    public async Task<GalleryResult> AddAsync(Guid runId, string? editKey, bool consent)
    {
        if (!Enabled) return new(false, "The gallery is switched off on this site.");
        if (!consent) return new(false, "Nothing was added: the gallery needs your yes first.");
        if (_runs.IsActive(runId) || _runs.IsDemoRun(runId) || !_runs.HasOwner(runId) || !_runs.CanEdit(runId, editKey))
            return new(false, "Only the browser that started this run can add it to the gallery.");
        var header = _runs.LoadHeader(runId);
        string source = _runs.FolderOf(runId);
        var described = header == null ? null : GallerySnapshot.Describe(source, header);
        var files = header == null ? Array.Empty<string>() : GallerySnapshot.FilesFor(header.Module);
        if (described == null || files.Count == 0) return new(false, "Only a finished review with a report can be added to the gallery.");
        if (files.Sum(f => File.Exists(Path.Join(source, f)) ? new FileInfo(Path.Join(source, f)).Length : 0) > GallerySnapshot.MaxBytes)
            return new(false, "This report is too large for the gallery.");

        await Gate.WaitAsync();
        try
        {
            var stored = AllStored();
            var same = stored.FirstOrDefault(e => e.Fingerprint == described.Fingerprint);
            if (same != null && _hidden.Contains(same.Id)) return new(false, "This review was taken down from the gallery by the site's administrator.");
            var now = DateTime.UtcNow;
            var entry = described with
            {
                Id = same?.Id ?? NewId(stored),
                AddedUtc = same?.AddedUtc ?? now,
                UpdatedUtc = same == null ? null : now,
                ConsentUtc = now,
                OwnerTokenSha256 = GalleryFingerprint.OwnerToken(editKey!),
            };
            WriteEntry(entry, source, files);
            DropOverCap(entry.Id);
            _log.LogInformation("Gallery entry {Entry} {Action} from run {Run}.", entry.Id, same == null ? "added" : "updated", entry.ShortRunId);
            return same == null
                ? new(true, "Added to the gallery.", entry)
                : new(true, "This review was already in the gallery; its report is now this run's.", entry, Updated: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Gallery entry not stored: {Message}", ex.Message);
            return new(false, "The report could not be added to the gallery. Try again later.");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Removes an entry for good, with the edit key of the run it was made from.</summary>
    public async Task<bool> WithdrawAsync(string? entryId, string? editKey)
    {
        if (!GalleryFingerprint.IsEntryId(entryId) || string.IsNullOrWhiteSpace(editKey) || editKey.Length > 100) return false;
        await Gate.WaitAsync();
        try
        {
            var entry = ReadEntry(Path.Join(Root, entryId!));
            if (entry == null || !CanWithdraw(entry, editKey)) return false;
            Directory.Delete(FolderOf(entry.Id), recursive: true);
            _log.LogInformation("Gallery entry {Entry} withdrawn by its owner.", entry.Id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Gallery entry not withdrawn: {Message}", ex.Message);
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>True when the key is the edit key of the run the entry was made from.</summary>
    public static bool CanWithdraw(GalleryEntry entry, string? editKey) =>
        !string.IsNullOrWhiteSpace(editKey) && editKey.Length <= 100 && entry.OwnerTokenSha256.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.OwnerTokenSha256), Encoding.ASCII.GetBytes(GalleryFingerprint.OwnerToken(editKey)));

    // The copy is written to a temporary folder and moved into place, so a reader never sees half an entry.
    private void WriteEntry(GalleryEntry entry, string source, IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(Root);
        string temp = Path.Join(Root, ".new-" + entry.Id);
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        foreach (var file in files)
        {
            string from = Path.Join(source, Path.GetFileName(file));
            if (File.Exists(from)) File.Copy(from, Path.Join(temp, Path.GetFileName(file)));
        }
        File.WriteAllText(Path.Join(temp, GalleryEntry.FileName), JsonSerializer.Serialize(entry, Json));
        string target = FolderOf(entry.Id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(temp, target);
    }

    private void DropOverCap(string keep)
    {
        var all = AllStored().OrderBy(e => e.LastChangedUtc).ToList();
        foreach (var old in all.Where(e => e.Id != keep).Take(Math.Max(0, all.Count - MaxEntries)))
        {
            Directory.Delete(FolderOf(old.Id), recursive: true);
            _log.LogInformation("Gallery entry {Entry} dropped: the gallery keeps {Max} reports.", old.Id, MaxEntries);
        }
    }

    private string NewId(IReadOnlyList<GalleryEntry> stored)
    {
        string id;
        do id = GalleryFingerprint.NewEntryId(); while (stored.Any(e => e.Id == id) || _hidden.Contains(id));
        return id;
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

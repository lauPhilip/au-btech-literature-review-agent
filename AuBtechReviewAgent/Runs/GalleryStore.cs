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
/// The gallery of example reports: finished runs whose owners said yes to publishing them. Each review is a folder,
/// App_Data/gallery/{entry id}/, holding entry.json and one sub-folder per version, v/{n}/, with a copy of that run's
/// report files (GallerySnapshot). It lives outside the run folders, as the metrics store does, so it is not removed by
/// the run clean-up.
///
/// The rules: nothing is added without the owner's explicit consent and edit key; one review has one entry, and a new
/// consent for the same fingerprint adds a version to it, so earlier versions keep their addresses; the gallery keeps
/// at most Gallery:MaxEntries versions in all (GalleryStore.Files.cs says which go first); whoever holds the edit key
/// of a version's run can withdraw the entry at any time, which removes every version; and an administrator hides an
/// entry with Gallery:HiddenIds.
/// </summary>
public sealed partial class GalleryStore
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
        MoveOldEntriesIntoVersions();
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

    /// <summary>The folder of one version's copy: {entry}/v/{number}.</summary>
    public string VersionFolderOf(string entryId, int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number), "Versions are numbered from 1.");
        return Path.Join(FolderOf(entryId), "v", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Every entry the site shows, as its newest version, the most recently changed first; hidden ones are left out.</summary>
    public IReadOnlyList<GalleryEntry> List() =>
        !Enabled ? Array.Empty<GalleryEntry>() : AllStored().Where(e => !_hidden.Contains(e.Id)).OrderByDescending(e => e.LastChangedUtc).ToList();

    /// <summary>One entry the site shows, as its newest version, or null (unknown, hidden or badly formed id).</summary>
    public GalleryEntry? Find(string? entryId) =>
        Enabled && GalleryFingerprint.IsEntryId(entryId) && !_hidden.Contains(entryId!) ? ReadEntry(Path.Join(Root, entryId!)) : null;

    /// <summary>One version of an entry the site shows, or null when the entry or the version is not there.</summary>
    public GalleryEntry? Find(string? entryId, int version) =>
        Find(entryId) is { } entry && entry.VersionOf(version) is { } v ? entry.ForVersion(v) : null;

    /// <summary>The shown entry with a version made from this run, so its report page can say it is in the gallery.</summary>
    public GalleryEntry? ForRun(Guid runId) => List().FirstOrDefault(e => e.Versions.Any(v => v.RunId == runId));

    /// <summary>
    /// Adds the run's report to the gallery, as a new entry or as the newest version of the same review. Refused without
    /// consent, without the run's edit key, for a run that is still going, the example run, or a run that did not
    /// finish with a report.
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
            var earlier = same?.Versions ?? new List<GalleryVersion>();
            if (earlier.FirstOrDefault(v => v.RunId == runId) is { } already)
                return new(true, $"This run is already in the gallery, as version {already.Number}.", same);
            var now = DateTime.UtcNow;
            int number = (same?.LastVersion ?? 0) + 1;
            var version = GalleryVersion.From(described, number, now, GalleryFingerprint.OwnerToken(editKey!));
            var entry = described with
            {
                Id = same?.Id ?? NewId(stored),
                AddedUtc = same?.AddedUtc ?? now,
                UpdatedUtc = same == null ? null : now,
                Versions = earlier.Append(version).ToList(),
                LastVersion = number,
                Version = number,
            };
            CopyVersion(entry.Id, number, source, files);
            WriteEntryFile(entry);
            DropOverCap(entry.Id);
            _log.LogInformation("Gallery entry {Entry} version {Version} added from run {Run}.", entry.Id, number, version.ShortRunId);
            return same == null
                ? new(true, "Added to the gallery.", entry)
                : new(true, $"This review was already in the gallery. This run is now its newest version, number {number}; the earlier versions stay.", entry, Updated: true);
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

    /// <summary>
    /// Removes the entry and every one of its versions for good, with the edit key of the run of any of its versions.
    /// Removing never publishes anything, so the key of one version is enough: a person who re-ran a review holds a
    /// different key for each run, and may no longer hold the oldest (browsers keep a key for 30 days).
    /// </summary>
    public async Task<bool> WithdrawAsync(string? entryId, string? editKey)
    {
        if (!GalleryFingerprint.IsEntryId(entryId) || string.IsNullOrWhiteSpace(editKey) || editKey.Length > 100) return false;
        await Gate.WaitAsync();
        try
        {
            var entry = ReadEntry(Path.Join(Root, entryId!));
            if (entry == null || !CanWithdraw(entry, editKey)) return false;
            Directory.Delete(FolderOf(entry.Id), recursive: true);
            _log.LogInformation("Gallery entry {Entry} and its {Count} version(s) withdrawn by an owner.", entry.Id, entry.Versions.Count);
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

    /// <summary>True when the key is the edit key of a run one of the entry's versions was made from.</summary>
    public static bool CanWithdraw(GalleryEntry entry, string? editKey) => entry.Versions.Any(v => OwnsVersion(v, editKey));

    private static bool OwnsVersion(GalleryVersion version, string? editKey) =>
        !string.IsNullOrWhiteSpace(editKey) && editKey.Length <= 100 && version.OwnerTokenSha256.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(version.OwnerTokenSha256), Encoding.ASCII.GetBytes(GalleryFingerprint.OwnerToken(editKey)));

    private string NewId(IReadOnlyList<GalleryEntry> stored)
    {
        string id;
        do id = GalleryFingerprint.NewEntryId(); while (stored.Any(e => e.Id == id) || _hidden.Contains(id));
        return id;
    }
}

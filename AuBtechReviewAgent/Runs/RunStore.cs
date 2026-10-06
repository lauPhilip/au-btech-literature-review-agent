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

/// <summary>
/// The run folders, for every kind of review: where a run lives, its header, who may change it, the reviewer's
/// notes, and deleting it. None of this depends on the module that made the run, so pages and modules use this
/// store instead of a review engine. Each run has its own folder, WorkspaceStore/{runId}/, named by the run id as 32
/// hex digits.
///
/// Edit keys: anyone with a run's link can open it and read the report, but only the browser that started the run
/// can delete it, submit its screening review, check a citation again or write notes. When a run starts, the
/// dashboard asks for an edit key: 32 random bytes that the browser keeps in its local storage and never shows. The
/// store keeps only the key's SHA-256 in owner.json, which is not part of the archive. There is deliberately no way
/// to see, copy or restore the key: a key that can be saved can also be shared or leaked, and a lost key only means
/// the run expires on its own after Runs:RetentionDays.
/// </summary>
public sealed class RunStore
{
    public const string OwnerFile = "owner.json";
    public const string NotesFile = "reviewer-notes.json";
    public const string ProtocolFile = "protocol.md";

    // Runs made before run.json existed are recognised by the ledger every one of them has (they were all
    // systematic reviews).
    private const string OlderRunLedger = "transparent-process.json";

    public const int MaxNoteLength = 1000;
    public const int MaxNotesPerRun = 500;

    private static readonly SemaphoreSlim NotesGate = new(1, 1);
    private static ILogger _log => AppLog.For<RunStore>();

    private readonly RunsOptions _options;
    private readonly Func<Guid, bool> _isActive;

    /// <param name="workspaceRoot">Folder holding one sub-folder per run.</param>
    /// <param name="options">Run settings (the demo run id).</param>
    /// <param name="isActive">Whether a run is still going; a running review cannot be changed or deleted.</param>
    public RunStore(string workspaceRoot, RunsOptions? options = null, Func<Guid, bool>? isActive = null)
    {
        WorkspaceRoot = workspaceRoot;
        _options = options ?? new RunsOptions();
        _isActive = isActive ?? (_ => false);
    }

    /// <summary>Folder holding one sub-folder per run.</summary>
    public string WorkspaceRoot { get; internal set; }

    /// <summary>
    /// The folder of one run. The run id comes from the URL, but as a Guid its "N" form is 32 hex digits, so it
    /// cannot contain a separator or "..". Path.GetFileName and the containment check below make that guarantee
    /// explicit, so the folder can never resolve outside the workspace even if this method is changed later.
    /// </summary>
    public string FolderOf(Guid runId)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot));
        string folder = Path.GetFullPath(Path.Join(root, Path.GetFileName(runId.ToString("N"))));
        if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("A run folder resolved outside the workspace.");
        return folder;
    }

    /// <summary>True when the run has started: its folder holds run.json, or the ledger of an older run.</summary>
    public bool HasRun(Guid runId)
    {
        string folder = FolderOf(runId);
        return File.Exists(Path.Join(folder, RunHeader.FileName)) || File.Exists(Path.Join(folder, OlderRunLedger));
    }

    public bool IsActive(Guid runId) => _isActive(runId);

    /// <summary>The run's header (run.json): its module, card and how it ended; null when the run does not exist.</summary>
    public RunHeader? LoadHeader(Guid runId) => RunHeader.Read(FolderOf(runId), runId);

    /// <summary>The protocol the run wrote before it searched, or null.</summary>
    public string? ReadProtocol(Guid runId)
    {
        string path = Path.Join(FolderOf(runId), ProtocolFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ── Edit keys ───────────────────────────────────────────────────────────────────────────────

    private sealed record OwnerRecord(string EditKeySha256, DateTime CreatedUtc);

    /// <summary>Creates the run folder and its edit key; returns the key, which only the starting browser keeps.</summary>
    public string ClaimRun(Guid runId)
    {
        string folder = FolderOf(runId);
        string path = Path.Join(folder, OwnerFile);
        if (File.Exists(path)) throw new InvalidOperationException("This run already has an owner.");
        Directory.CreateDirectory(folder);
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, JsonSerializer.Serialize(new OwnerRecord(HashKey(key), DateTime.UtcNow)));
        return key;
    }

    /// <summary>
    /// True when the key belongs to the run. Runs started before edit keys existed have no owner.json and stay
    /// editable by anyone with the link, as before, until they expire.
    /// </summary>
    public bool CanEdit(Guid runId, string? editKey)
    {
        if (IsDemoRun(runId)) return false; // the example report stays as it is for everyone
        string folder = FolderOf(runId);
        string path = Path.Join(folder, OwnerFile);
        if (!File.Exists(path)) return Directory.Exists(folder);
        if (string.IsNullOrWhiteSpace(editKey) || editKey.Length > 100) return false;
        try
        {
            var owner = JsonSerializer.Deserialize<OwnerRecord>(File.ReadAllText(path));
            if (owner == null) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(owner.EditKeySha256), Encoding.ASCII.GetBytes(HashKey(editKey)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Run {RunId}: owner record unreadable: {Message}", runId, ex.Message);
            return false;
        }
    }

    /// <summary>True when the run has an owner record, i.e. it can only be changed with its edit key.</summary>
    public bool HasOwner(Guid runId) => File.Exists(Path.Join(FolderOf(runId), OwnerFile));

    /// <summary>The example run from Runs:DemoRunId, when it is set and the run still exists.</summary>
    public Guid? DemoRunId =>
        Guid.TryParse(_options.DemoRunId, out var id) && id != Guid.Empty && HasRun(id) ? id : null;

    public bool IsDemoRun(Guid runId) => Guid.TryParse(_options.DemoRunId, out var id) && id == runId;

    private static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    // ── Reviewer notes ──────────────────────────────────────────────────────────────────────────

    /// <summary>The reviewer's notes on the run (reviewer-notes.json, part of the archive).</summary>
    public IReadOnlyList<ReviewerNote> LoadNotes(Guid runId)
    {
        string path = Path.Join(FolderOf(runId), NotesFile);
        if (!File.Exists(path)) return Array.Empty<ReviewerNote>();
        try { return JsonSerializer.Deserialize<List<ReviewerNote>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Run {RunId}: reviewer notes unreadable: {Message}", runId, ex.Message);
            return Array.Empty<ReviewerNote>();
        }
    }

    /// <summary>
    /// Saves, replaces or (with empty text) removes one note. Returns false without the run's edit key, for a run
    /// that does not exist or is still going, or when the run already has the maximum number of notes.
    /// </summary>
    public async Task<bool> SaveNoteAsync(Guid runId, string? editKey, string kind, int reference, string text, string field = "", int sentenceIndex = -1)
    {
        if (kind is not (ReviewerNote.Citation or ReviewerNote.Study) || reference < 1) return false;
        if (IsActive(runId) || !CanEdit(runId, editKey) || !HasRun(runId)) return false;

        string clean = ReviewInputGuard.Normalize(text, singleLine: false);
        if (clean.Length > MaxNoteLength) clean = clean[..MaxNoteLength];
        if (kind == ReviewerNote.Study) { field = ""; sentenceIndex = -1; }

        await NotesGate.WaitAsync();
        try
        {
            var notes = LoadNotes(runId).Where(n => !n.IsFor(kind, reference, field, sentenceIndex)).ToList();
            if (clean.Length > 0)
            {
                if (notes.Count >= MaxNotesPerRun) return false;
                notes.Add(new ReviewerNote(kind, reference, field, sentenceIndex, clean, DateTime.UtcNow));
            }
            var ordered = notes.OrderBy(n => n.Kind, StringComparer.Ordinal).ThenBy(n => n.Reference)
                .ThenBy(n => n.Field, StringComparer.Ordinal).ThenBy(n => n.SentenceIndex).ToList();
            await SafeFile.WriteAllTextAsync(Path.Join(FolderOf(runId), NotesFile),
                JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        finally
        {
            NotesGate.Release();
        }
    }

    // ── Deleting ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Deletes the run's folder; only with its edit key, and never while the run is going.</summary>
    public bool DeleteRun(Guid runId, string? editKey = null)
    {
        if (IsActive(runId) || !CanEdit(runId, editKey)) return false;
        string folder = FolderOf(runId);
        if (!Directory.Exists(folder)) return false;
        try
        {
            Directory.Delete(folder, recursive: true);
            _log.LogInformation("Run {RunId} deleted by its user.", runId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run {RunId} could not be deleted: {Message}", runId, ex.Message);
            return false;
        }
    }
}

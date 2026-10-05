using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Who may change a run. Anyone with a run's link can open it and read the report, but only the browser that
/// started the run can delete it, submit its screening review or check a citation again. When a run starts, the
/// dashboard asks for an edit key: 32 random bytes that the browser keeps in its local storage and never shows.
/// The server keeps only the key's SHA-256 in owner.json in the run folder, which is not part of the archive.
/// There is deliberately no way to see, copy or restore the key: a key that can be saved can also be shared or
/// leaked, and a lost key only means the run expires on its own after Runs:RetentionDays.
/// </summary>
public partial class PrismaReviewEngine
{
    public const string OwnerFile = "owner.json";

    private sealed record OwnerRecord(string EditKeySha256, DateTime CreatedUtc);

    /// <summary>Creates the run folder and its edit key; returns the key, which only the starting browser keeps.</summary>
    public string ClaimRun(Guid runId)
    {
        string folder = GetWorkspaceFolderPath(runId);
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
        string folder = GetWorkspaceFolderPath(runId);
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

    /// <summary>The example run from Runs:DemoRunId, when it is set and the run still exists.</summary>
    public Guid? DemoRunId =>
        Guid.TryParse(_runsOptions.DemoRunId, out var id) && id != Guid.Empty && File.Exists(GetStateFilePath(id)) ? id : null;

    public bool IsDemoRun(Guid runId) => Guid.TryParse(_runsOptions.DemoRunId, out var id) && id == runId;

    /// <summary>True when the run has an owner record, i.e. it can only be changed with its edit key.</summary>
    public bool HasOwner(Guid runId) => File.Exists(Path.Join(GetWorkspaceFolderPath(runId), OwnerFile));

    private static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

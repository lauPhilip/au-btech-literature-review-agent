using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The header of a run folder (run.json): which module made the run, from which card, when it started, and how it
/// ended. It is written when the run starts (stage "Queued") and again when it ends (its final stage); the live stage
/// of a running review stays in the module's ledger. It is the first file in the folder, so code that only needs to
/// list, open, clean up or redirect a run reads this small file and never has to understand a module's own ledger.
///
/// Runs made before modules existed have no run.json; they were all systematic reviews, so they read as one.
/// </summary>
public sealed record RunHeader(
    Guid RunId,
    string Module,
    string Card,
    DateTime StartedUtc,
    string Stage = "",
    DateTime? CompletedUtc = null)
{
    public const string FileName = "run.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Task WriteAsync(string runFolder, RunHeader header) =>
        SafeFile.WriteAllTextAsync(Path.Join(runFolder, FileName), header.ToJson());

    /// <summary>
    /// The header of the run in this folder; null when the folder does not exist. A folder without a readable
    /// run.json is an older run and reads as a systematic review.
    /// </summary>
    public static RunHeader? Read(string runFolder, Guid runId)
    {
        if (!Directory.Exists(runFolder)) return null;
        string path = Path.Join(runFolder, FileName);
        if (File.Exists(path))
        {
            try
            {
                var header = JsonSerializer.Deserialize<RunHeader>(File.ReadAllText(path), Json);
                if (header != null && !string.IsNullOrWhiteSpace(header.Module)) return header;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Falls through to the older-run reading below; the folder's own ledger is still intact.
            }
        }
        return new RunHeader(runId, ReviewModules.Default.Key, ReviewMethod.SystematicKey, Directory.GetCreationTimeUtc(runFolder));
    }
}

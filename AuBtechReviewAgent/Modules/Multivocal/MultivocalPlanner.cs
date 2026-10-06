using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>The planning record of a multivocal run (multivocal-plan.json): the answers, and the protocol's fingerprint.</summary>
public sealed record PlannedRun(Guid RunId, MultivocalPlan Plan, string ProtocolSha256, DateTime CreatedUtc);

/// <summary>
/// Starts a multivocal run from a complete plan: claims the run folder (the edit key goes to the browser only), then
/// writes run.json, the protocol and the plan, in that order and before anything is searched. Searching comes with
/// MLR block D; until then a run ends at the "Planned" stage.
/// </summary>
public sealed class MultivocalPlanner
{
    public const string PlanFile = "multivocal-plan.json";
    public const string StagePlanned = "Planned";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly RunStore _runs;

    public MultivocalPlanner(RunStore runs) => _runs = runs;

    /// <summary>Writes the run; returns its id and edit key. Throws <see cref="ArgumentException"/> when the plan is incomplete.</summary>
    public async Task<(Guid RunId, string EditKey)> CreateAsync(MultivocalPlan plan)
    {
        plan.Normalized();
        var problems = plan.Problems();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));

        var runId = Guid.NewGuid();
        string editKey = _runs.ClaimRun(runId);
        string folder = _runs.FolderOf(runId);
        var now = DateTime.UtcNow;

        await RunHeader.WriteAsync(folder, new RunHeader(runId, "multivocal", plan.Card, now, StagePlanned));
        string protocol = MultivocalProtocol.Write(plan, runId, now);
        await SafeFile.WriteAllTextAsync(Path.Join(folder, RunStore.ProtocolFile), protocol);
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(protocol))).ToLowerInvariant();
        await SafeFile.WriteAllTextAsync(Path.Join(folder, PlanFile), JsonSerializer.Serialize(new PlannedRun(runId, plan, sha, now), Json));
        return (runId, editKey);
    }

    /// <summary>The plan of a multivocal run, or null when the run does not exist or is not a multivocal run.</summary>
    public PlannedRun? Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), PlanFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<PlannedRun>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }
}

using System;
using System.Collections.Generic;
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

    /// <summary>The seed step's settings (OpenSources:SeedStudies and SeedChainingPerDirection), for the protocol; 0 when the step is off.</summary>
    public int SeedStudies { get; init; }
    public int SeedChainingPerDirection { get; init; }

    /// <summary>
    /// Writes the run; returns its id and edit key. Throws <see cref="ArgumentException"/> when the plan is incomplete,
    /// or when its text has instruction-like phrases (<see cref="MultivocalPlanGuard"/>) the reviewer has not confirmed
    /// in <paramref name="confirmedFlags"/>. The flags are worked out here again, so the browser cannot leave one out.
    /// </summary>
    public async Task<(Guid RunId, string EditKey)> CreateAsync(MultivocalPlan plan, IReadOnlyList<string>? confirmedFlags = null)
    {
        // The flags first, from the plan as typed, as the form worked them out: cleaning takes out tag characters,
        // and with them the hidden text the flags must still name.
        var flags = MultivocalPlanGuard.Lines(plan);
        plan.Normalized();
        var problems = plan.Problems();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        if (flags.Count > 0 && (confirmedFlags == null || !flags.SequenceEqual(confirmedFlags)))
            throw new ArgumentException("Some of your text reads like instructions to the language model. Confirm it on the last step, or change it.");
        plan.InputFlags = flags;

        var runId = Guid.NewGuid();
        string editKey = _runs.ClaimRun(runId);
        string folder = _runs.FolderOf(runId);
        var now = DateTime.UtcNow;

        await RunHeader.WriteAsync(folder, new RunHeader(runId, "multivocal", plan.Card, now, StagePlanned));
        string protocol = MultivocalProtocol.Write(plan, runId, now, SeedStudies, SeedChainingPerDirection);
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

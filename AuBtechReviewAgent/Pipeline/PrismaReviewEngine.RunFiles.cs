using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>Per-run working data that is not part of the ledger (the full paper records and their text).</summary>
    private sealed class RunContext
    {
        public required Guid RunId { get; init; }
        public required ReviewRequest Request { get; init; }
        public required ReviewState State { get; init; }
        public required RecordingChatCompletionService Chat { get; init; }
        public required string Workspace { get; init; }
        /// <summary>Cancelled when the run's owner cancels the run, through CancelRun.</summary>
        public CancellationToken Stop { get; init; }
        public List<IAcademicSource> Sources { get; } = new();
        public Dictionary<string, AcademicPaper> Papers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<DocumentChunk>> Chunks { get; } = new(StringComparer.Ordinal);

        // Duplicate detection across all sources and citation chaining (source id, DOI, normalised title -> first id seen).
        public Dictionary<string, string> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SeenDois { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SeenTitles { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Screening results of both prompts, for Cohen's kappa.
        public List<(bool First, bool Second)> DualPairs { get; } = new();

        // Search progress: passes finished (updated from parallel source tasks) and planned.
        public int SearchPassesDone;
        public int SearchPassesTotal;
    }

    private async Task PublishAsync(RunContext ctx)
    {
        await SaveStateAsync(ctx.RunId, ctx.State);
        OnProgressUpdated?.Invoke(ctx.RunId, ctx.State.Stats);
    }

    /// <summary>Waits for a run slot, showing the run's place in line while it waits; a cancelled run leaves the line.</summary>
    private async Task<IDisposable> AcquireSlotAsync(RunContext ctx)
    {
        var acquire = Coordinator.AcquireSlotAsync(ctx.RunId, ctx.Stop);
        string stageBefore = ctx.State.Stats.ProcessingStage;
        while (!acquire.IsCompleted)
        {
            int position = Coordinator.QueuePosition(ctx.RunId);
            if (position > 0 && (position != ctx.State.Stats.QueuePosition || ctx.State.Stats.ProcessingStage != StageQueued))
            {
                ctx.State.Stats.QueuePosition = position;
                ctx.State.Stats.ProcessingStage = StageQueued;
                await PublishAsync(ctx);
            }
            await Task.WhenAny(acquire, Task.Delay(1000));
        }
        if (ctx.State.Stats.QueuePosition != 0)
        {
            ctx.State.Stats.QueuePosition = 0;
            ctx.State.Stats.ProcessingStage = stageBefore;
        }
        return await acquire;
    }

    /// <summary>
    /// Call once at startup. Runs cannot survive an app restart (IIS recycles the app pool on idle and on a
    /// schedule), so any ledger still in a working stage is marked "Interrupted" instead of spinning forever.
    /// </summary>
    public int MarkInterruptedRuns()
    {
        string store = WorkspaceRoot;
        if (!Directory.Exists(store)) return 0;
        int marked = 0;
        foreach (var dir in Directory.GetDirectories(store))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var runId) || IsRunActive(runId)) continue;
            var state = LoadState(runId);
            if (state == null || IsFinishedStage(state.Stats.ProcessingStage) || state.Stats.ProcessingStage == "Idle") continue;
            state.Stats.ProcessingStage = StageInterrupted;
            state.Stats.QueuePosition = 0;
            state.FailureMessage = "The server restarted while this run was in progress, so it could not finish. Please start it again.";
            state.CompletedUtc = DateTime.UtcNow;
            try
            {
                File.WriteAllText(GetStateFilePath(runId), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                string headerPath = Path.Join(dir, RunHeader.FileName);
                if (File.Exists(headerPath) && RunHeader.Read(dir, runId) is { } header)
                    File.WriteAllText(headerPath, (header with { Stage = StageInterrupted, CompletedUtc = state.CompletedUtc }).ToJson());
                marked++;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not mark an interrupted run: {Message}", SanitizeLogMessage(ex.Message));
            }
        }
        return marked;
    }

    /// <summary>The run's header (run.json): its module, card and stage; null when the run does not exist.</summary>
    public RunHeader? LoadHeader(Guid runId) => Store.LoadHeader(runId);

    /// <summary>Reads a run's ledger from disk, or null if there is none (e.g. expired and deleted).</summary>
    public ReviewState? LoadState(Guid runId)
    {
        string path = GetStateFilePath(runId);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ReviewState>(stream);
        }
        catch
        {
            return null;
        }
    }

    // Written to a temporary file and moved into place, so a dashboard reading the ledger never sees half a file.
    private async Task SaveStateAsync(Guid sessionId, ReviewState state)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await SafeFile.WriteAllTextAsync(GetStateFilePath(sessionId), JsonSerializer.Serialize(state, options));
    }

    /// <summary>
    /// Deletes a finished run's folder (ledger, report, source PDFs). Anyone with the run link can open a
    /// run, but only the browser that started it (with its edit key) can remove it. Refuses while the run is still active.
    /// </summary>
    public bool DeleteRun(Guid runId, string? editKey = null) => Store.DeleteRun(runId, editKey);

    /// <summary>The run's protocol.md, or null when there is none.</summary>
    public string? ReadProtocol(Guid runId) => Store.ReadProtocol(runId);

    /// <summary>Writes protocol.md before the search starts and records its fingerprint in the ledger.</summary>
    private async Task WriteProtocolAsync(RunContext ctx)
    {
        var names = ctx.State.SelectedSources.Except(ctx.State.UnavailableSources).Select(SourceCatalog.DisplayNameFor).ToList();
        string text = ProtocolWriter.Write(ctx.RunId, ctx.Request with { UserKeys = null }, names, Llm.DisplayNameFor("screening"), _runsOptions, DateTime.UtcNow, ctx.State.InputFlags);
        string path = Path.Join(ctx.Workspace, "protocol.md");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        ctx.State.ProtocolSha256 = RunManifest.Sha256(File.ReadAllBytes(path));
    }
}

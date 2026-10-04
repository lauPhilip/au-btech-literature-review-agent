using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// GET /health: whether the app can do its work, for uptime monitoring. Checks what can be checked without
/// spending anything: the run folder is writable and has room, a model is configured (the model itself is
/// not called, that would cost money on every probe), and how the scholarly databases answered lately.
/// "unhealthy" (HTTP 503) only when runs cannot be stored; slow or rate-limited databases make it "degraded".
/// No keys, paths or visitor data are included.
/// </summary>
public static class SiteHealth
{
    public const long LowDiskBytes = 500L * 1024 * 1024;
    public const long CriticalDiskBytes = 100L * 1024 * 1024;

    public sealed record Check(string Name, string Status, string Detail);
    public sealed record Report(string Status, string Version, DateTime CheckedUtc, int ActiveRuns, IReadOnlyList<Check> Checks);

    public static Report Build(PrismaReviewEngine engine, bool modelKeyConfigured, DateTime utcNow)
    {
        var checks = new List<Check> { Disk(engine.WorkspaceRoot), Model(engine.Llm, modelKeyConfigured) };
        checks.AddRange(Sources());

        string status = checks.Any(c => c.Status == "unhealthy") ? "unhealthy"
            : checks.Any(c => c.Status == "degraded") ? "degraded" : "ok";
        return new Report(status, RecordingChatCompletionService.AppVersion.Split('+')[0], utcNow,
            engine.Coordinator.ActiveRunIds.Count, checks);
    }

    /// <summary>The run folder can be written to, and the drive has room for more runs.</summary>
    public static Check Disk(string workspaceRoot, Func<string, long?>? freeBytes = null)
    {
        try
        {
            Directory.CreateDirectory(workspaceRoot);
            string probe = Path.Join(workspaceRoot, $".health-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Check("storage", "unhealthy", "The run folder cannot be written to, so new runs cannot be stored.");
        }

        long? free = (freeBytes ?? FreeBytes)(workspaceRoot);
        if (free == null) return new Check("storage", "ok", "The run folder is writable (free space unknown).");
        string mb = $"{free.Value / (1024 * 1024):N0} MB free";
        return free.Value < CriticalDiskBytes ? new Check("storage", "unhealthy", $"Almost no disk space left ({mb}).")
             : free.Value < LowDiskBytes ? new Check("storage", "degraded", $"Disk space is running low ({mb}).")
             : new Check("storage", "ok", $"The run folder is writable; {mb}.");
    }

    private static long? FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // shared hosting may not allow asking
        }
    }

    public static Check Model(LlmOptions llm, bool keyConfigured) =>
        llm.IsOpenAICompatible || keyConfigured
            ? new Check("model", "ok", $"{llm.DisplayName} is configured (not called by this check).")
            : new Check("model", "unhealthy", "No model key is configured, so runs on the server key cannot start.");

    /// <summary>One line per database that has been asked something since the app started.</summary>
    public static IEnumerable<Check> Sources()
    {
        var now = SourceStatus.UtcNow();
        foreach (var h in SourceStatus.Snapshot())
        {
            bool recentProblem = h.LastProblemUtc is { } t && now - t <= SourceStatus.RecentWindow && h.LastProblem != SourceStatus.Problem.None;
            string name = SourceStatus.DisplayName(h.Host);
            string last = h.LastOkUtc is { } ok ? $"last answered {Ago(now - ok)}" : "no successful answer yet";
            yield return recentProblem
                ? new Check($"source:{name}", "degraded", $"{h.LastProblem} {Ago(now - h.LastProblemUtc!.Value)} ({h.RecentProblems} problem(s) in the last 30 min); {last}.")
                : new Check($"source:{name}", "ok", $"Answering normally; {last}.");
        }
    }

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h ago";
}

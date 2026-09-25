using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// Deletes run folders (WorkspaceStore/{runId}) once they are older than Runs:RetentionDays. A folder's age
/// is the newest write time of any file inside it: the folder's own timestamp does not change when the
/// ledger inside is rewritten, which is why runs used to be deleted 30-60 minutes after they started,
/// sometimes while still running. Runs that are active in this process are never deleted.
/// </summary>
public class SessionCleanupWorker : BackgroundService
{
    private readonly ILogger<SessionCleanupWorker> _logger;
    private readonly PrismaReviewEngine _engine;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public SessionCleanupWorker(ILogger<SessionCleanupWorker> logger, PrismaReviewEngine engine)
    {
        _logger = logger;
        _engine = engine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Run retention: results are kept for {Days} days.", _engine.RunsOptions.RetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PurgeExpiredRuns(_engine.WorkspaceRoot,
                    TimeSpan.FromDays(Math.Max(1, _engine.RunsOptions.RetentionDays)), DateTime.UtcNow, _engine.IsRunActive, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the run cleanup pass.");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    /// <summary>Deletes expired run folders and returns how many were deleted. Static so it can be unit-tested.</summary>
    public static int PurgeExpiredRuns(string workspaceStore, TimeSpan retention, DateTime utcNow, Func<Guid, bool> isActive, ILogger? logger = null)
    {
        if (!Directory.Exists(workspaceStore)) return 0;
        DateTime cutoff = utcNow - retention;
        int deleted = 0;

        foreach (var dir in Directory.GetDirectories(workspaceStore))
        {
            if (Guid.TryParseExact(Path.GetFileName(dir), "N", out var runId) && isActive(runId)) continue;

            DateTime lastActivity = LastActivityUtc(dir);
            if (lastActivity >= cutoff) continue;

            try
            {
                Directory.Delete(dir, true);
                deleted++;
                logger?.LogInformation("Deleted expired run folder {DirName} (last activity {Last:u}).", Path.GetFileName(dir), lastActivity);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("Skipped deleting run folder {DirName}: {Message}", Path.GetFileName(dir), ex.Message);
            }
        }
        return deleted;
    }

    public static DateTime LastActivityUtc(string dir)
    {
        DateTime newest = Directory.GetLastWriteTimeUtc(dir);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            DateTime t = File.GetLastWriteTimeUtc(file);
            if (t > newest) newest = t;
        }
        return newest;
    }
}

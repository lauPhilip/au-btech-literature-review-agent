using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

public class SessionCleanupWorker : BackgroundService
{
    private readonly ILogger<SessionCleanupWorker> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _sessionExpiration = TimeSpan.FromMinutes(30);

    public SessionCleanupWorker(ILogger<SessionCleanupWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PRISMA Storage Lifecycle Engine activated.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PurgeExpiredSessions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the session cleanup pass.");
            }

            // Wait for 1 hour before evaluating file lifecycles again
            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private void PurgeExpiredSessions()
    {
        string rootDir = Directory.GetCurrentDirectory();
        DateTime cutoffTime = DateTime.UtcNow - _sessionExpiration;

        // 1. Purge expired JSON state ledgers
        var stateFiles = Directory.GetFiles(rootDir, "transparent-process-*.json");
        foreach (var file in stateFiles)
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffTime)
            {
                TryDeleteFile(file);
            }
        }

        // 2. Purge expired JSON checklist reports
        var reportFiles = Directory.GetFiles(rootDir, "prisma-report-*.json");
        foreach (var file in reportFiles)
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffTime)
            {
                TryDeleteFile(file);
            }
        }

        // 3. Purge expired workspace folders containing downloaded PDFs
        string workspaceStore = Path.Combine(rootDir, "WorkspaceStore");
        if (Directory.Exists(workspaceStore))
        {
            var sessionDirs = Directory.GetDirectories(workspaceStore);
            foreach (var dir in sessionDirs)
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoffTime)
                {
                    TryDeleteDirectory(dir);
                }
            }
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            _logger.LogInformation("Cleaned up expired tracking artifact: {FileName}", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Skipped file purge (locked or active channel): {Message}", ex.Message);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
            _logger.LogInformation("Purged expired user download sandbox container: {DirName}", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Skipped directory purge (active system write hold): {Message}", ex.Message);
        }
    }
}
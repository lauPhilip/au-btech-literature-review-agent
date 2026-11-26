using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace AuBtechReviewAgent;

/// <summary>
/// GET /api/workspace/{id}/deposit: the deposit archive of a run, for Zenodo or OSF (<see cref="DepositArchive"/>). It is
/// made from the owner's archive, kept or written to a file as the archive download does (<see cref="RunArchiveCache"/>),
/// and written to a temporary file in the run folder that is deleted when the download ends; the .tmp name keeps it out of the
/// archive, the folder's stamp and the clean-up's age of the run. Like the archive, it answers 304 for an unchanged run.
/// </summary>
public static class DepositDownload
{
    public static void MapDepositDownload(this WebApplication app, PrismaReviewEngine engine, MultivocalWriter writer)
    {
        var runs = engine.Store;
        app.MapGet("/api/workspace/{sessionId:guid}/deposit", (Guid sessionId, HttpContext http) =>
        {
            string folder = runs.FolderOf(sessionId);
            var stamp = RunFolderStamp.Of(folder);
            if (stamp == null) return Results.NotFound();
            if (WorkspaceDownloads.IsNotModified(http.Request, stamp)) // answered 304 by Serve before it builds anything
                return WorkspaceDownloads.Serve(runs, http, sessionId, () => null, "application/zip", null);

            var header = runs.LoadHeader(sessionId);
            bool multivocal = header?.Module == "multivocal";
            using var owner = RunArchiveCache.GetOrBuild(folder, stamp, RunArchiveCache.IsFinished(header, runs.IsActive(sessionId)), output =>
            {
                multivocal = false;
                if (engine.WriteWorkspaceArchive(sessionId, output)) return true;
                multivocal = writer.WriteArchive(sessionId, output); // a multivocal run has no systematic review state
                return multivocal;
            });
            if (owner == null) return Results.NotFound();

            var deposit = new FileStream(Path.Join(folder, $"deposit-{Guid.NewGuid():N}.zip.tmp"), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.DeleteOnClose);
            try
            {
                DepositArchive.Write(owner, deposit);
                deposit.Position = 0;
            }
            catch
            {
                deposit.Dispose();
                throw;
            }
            string fileName = multivocal ? $"MLR_Deposit_{DateTime.UtcNow:yyyyMMdd}.zip" : $"PRISMA_Deposit_{DateTime.UtcNow:yyyyMMdd}.zip";
            return Results.File(deposit, contentType: "application/zip", fileDownloadName: fileName,
                lastModified: stamp.LastModified, entityTag: new EntityTagHeaderValue(stamp.ETag));
        }).RequireRateLimiting(SiteRateLimits.DownloadPolicy);
    }
}

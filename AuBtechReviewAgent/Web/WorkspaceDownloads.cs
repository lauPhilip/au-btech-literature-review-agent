using System;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace AuBtechReviewAgent;

/// <summary>
/// The run downloads under /api/workspace/{id}/: the archive, the references, every screened record, the protocol,
/// a multivocal run's main.tex and its report. Each answer carries Last-Modified and an ETag from the run folder's
/// <see cref="RunFolderStamp"/>, so a repeat download of an unchanged run is answered 304 Not Modified without
/// building anything (issue #306). A finished run's archive is kept in its folder (<see cref="RunArchiveCache"/>).
/// The handlers use the services passed in here instead of taking them as handler parameters: every handler
/// parameter counts as request input for static analysis, and the services are not.
/// </summary>
public static class WorkspaceDownloads
{
    public static void MapWorkspaceDownloads(this WebApplication app, PrismaReviewEngine engine, MultivocalWriter writer, MultivocalReporter reporter)
    {
        var runs = engine.Store;

        // Served as a real HTTP download, not through the Blazor connection, whose small message size fails once a
        // run holds real source PDFs.
        app.MapGet("/api/workspace/{sessionId:guid}/archive", (Guid sessionId, HttpContext http) =>
        {
            string folder = runs.FolderOf(sessionId);
            var stamp = RunFolderStamp.Of(folder);
            if (stamp == null) return Results.NotFound();
            if (IsNotModified(http.Request, stamp)) return NotModified(http.Response, stamp);

            var header = runs.LoadHeader(sessionId);
            bool multivocal = header?.Module == "multivocal";
            var zip = RunArchiveCache.GetOrBuild(folder, stamp, RunArchiveCache.IsFinished(header, runs.IsActive(sessionId)), () =>
            {
                byte[] built = engine.GenerateWorkspaceArchiveFromDisk(sessionId);
                multivocal = false;
                if (built.Length == 0)
                {
                    built = writer.Archive(sessionId); // a multivocal run has no systematic review state
                    multivocal = built.Length > 0;
                }
                return built;
            });
            if (zip.Length == 0)
            {
                zip.Dispose();
                return Results.NotFound();
            }
            string fileName = multivocal ? $"MLR_Run_Archive_{DateTime.UtcNow:yyyyMMdd}.zip" : $"PRISMA_Evaluation_Footprint_{DateTime.UtcNow:yyyyMMdd}.zip";
            return Results.File(zip, contentType: "application/zip", fileDownloadName: fileName,
                lastModified: stamp.LastModified, entityTag: new EntityTagHeaderValue(stamp.ETag));
        }).RequireRateLimiting("ArchiveDownloadPolicy");

        // The included papers (or a multivocal run's grey sources) as BibTeX or RIS, for Zotero / EndNote / Mendeley.
        app.MapGet("/api/workspace/{sessionId:guid}/references.{format}", (Guid sessionId, string format, HttpContext http) =>
        {
            if (format is not ("bib" or "ris")) return Results.NotFound();
            string mime = format == "bib" ? "application/x-bibtex" : "application/x-research-info-systems";
            return Serve(runs, http, sessionId, () => Utf8(engine.ExportReferences(sessionId, format) ?? reporter.ExportReferences(sessionId, format)),
                mime, $"references.{format}");
        }).RequireRateLimiting("ArchiveDownloadPolicy");

        // Every screened record as RIS, tagged "included" or "excluded: reason", for a Zotero library of the screening.
        app.MapGet("/api/workspace/{sessionId:guid}/screened.ris", (Guid sessionId, HttpContext http) =>
            Serve(runs, http, sessionId, () => Utf8(engine.ExportReferences(sessionId, "screened") ?? reporter.ExportReferences(sessionId, "screened")),
                "application/x-research-info-systems", "screened.ris"))
            .RequireRateLimiting("ArchiveDownloadPolicy");

        // The protocol written before the search (PRISMA item 24), linked from the Review Output page; shown, not saved.
        app.MapGet("/api/workspace/{sessionId:guid}/protocol.md", (Guid sessionId, HttpContext http) =>
            Serve(runs, http, sessionId, () => Utf8(engine.Store.ReadProtocol(sessionId)), "text/markdown; charset=utf-8", null))
            .RequireRateLimiting("ArchiveDownloadPolicy");

        // The paper of a checked multivocal run as LaTeX, built in code from its files (MLR G-8).
        app.MapGet("/api/workspace/{sessionId:guid}/main.tex", (Guid sessionId, HttpContext http) =>
            Serve(runs, http, sessionId, () => Utf8(writer.MainTex(sessionId)), "application/x-tex; charset=utf-8", MultivocalPaper.MainTexFile))
            .RequireRateLimiting("ArchiveDownloadPolicy");

        // The report of a synthesised multivocal run, written in code from its files (MLR block G).
        app.MapGet("/api/workspace/{sessionId:guid}/mlr-report.md", (Guid sessionId, HttpContext http) =>
            Serve(runs, http, sessionId, () => Utf8(reporter.Build(sessionId)?.ToMarkdown()), "text/markdown; charset=utf-8", MultivocalReporter.ReportFile))
            .RequireRateLimiting("ArchiveDownloadPolicy");
    }

    private static byte[]? Utf8(string? text) => text == null ? null : new UTF8Encoding(false).GetBytes(text);

    /// <summary>
    /// One download built from a run's files: 404 when the run or the file does not exist, 304 when the browser has
    /// it already, else the file with Last-Modified and an ETag.
    /// </summary>
    public static IResult Serve(RunStore runs, HttpContext http, Guid runId, Func<byte[]?> build, string contentType, string? fileName)
    {
        var stamp = RunFolderStamp.Of(runs.FolderOf(runId));
        if (stamp == null) return Results.NotFound();
        if (IsNotModified(http.Request, stamp)) return NotModified(http.Response, stamp);
        byte[]? content = build();
        return content == null ? Results.NotFound()
            : Results.File(content, contentType: contentType, fileDownloadName: fileName,
                lastModified: stamp.LastModified, entityTag: new EntityTagHeaderValue(stamp.ETag));
    }

    /// <summary>
    /// Whether the browser's copy is current (RFC 9110, section 13.2.2): If-None-Match decides when it is sent,
    /// else If-Modified-Since. Checked before anything is built, so a 304 costs only the folder's stamp.
    /// </summary>
    public static bool IsNotModified(HttpRequest request, RunFolderStamp stamp)
    {
        var headers = request.GetTypedHeaders();
        if (headers.IfNoneMatch is { Count: > 0 } tags)
        {
            var current = new EntityTagHeaderValue(stamp.ETag);
            return tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(current, useStrongComparison: false));
        }
        return headers.IfModifiedSince is { } since && stamp.LastModified <= since;
    }

    private static IResult NotModified(HttpResponse response, RunFolderStamp stamp)
    {
        var headers = response.GetTypedHeaders();
        headers.ETag = new EntityTagHeaderValue(stamp.ETag);
        headers.LastModified = stamp.LastModified;
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }
}

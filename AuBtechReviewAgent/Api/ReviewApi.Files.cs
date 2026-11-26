using System;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>One file of a finished review: its kind, name and type, its address, and its text when it is text.</summary>
public sealed record ApiReviewFile(Guid Id, string Kind, string FileName, string MediaType, string Address, string? Content);

// The files of a finished review, the same ones the download endpoints serve, for the MCP server's get_review_file.
public sealed partial class ReviewApi
{
    /// <summary>The kinds of file a caller may ask for.</summary>
    public static readonly string[] FileKinds = { "bibtex", "ris", "main.tex", "archive" };

    public ApiResult<ApiReviewFile> FileOf(Guid runId, string? kind, string baseUrl)
    {
        string wanted = (kind ?? "").Trim().ToLowerInvariant();
        if (!FileKinds.Contains(wanted))
            return ApiProblems.Invalid("Ask for one of the files a review has.", new[] { $"File \"{Clip(kind ?? "")}\" is not one of: {string.Join(", ", FileKinds)}." });
        var header = _engine.LoadHeader(runId);
        if (header == null) return ApiProblems.NoRun();
        if (header.Module != ReviewMethod.SystematicKey) return ApiProblems.NotSystematic();
        string stage = _engine.LoadState(runId)?.Stats.ProcessingStage ?? PrismaReviewEngine.StageQueued;
        if (stage != PrismaReviewEngine.StageComplete) return ApiProblems.NotReady(stage);

        var downloads = DownloadsFor(runId, baseUrl);
        ApiReviewFile? file = wanted switch
        {
            "bibtex" => Text(runId, wanted, "references.bib", "application/x-bibtex", downloads.BibTex, _engine.ExportReferences(runId, "bib")),
            "ris" => Text(runId, wanted, "references.ris", "application/x-research-info-systems", downloads.Ris, _engine.ExportReferences(runId, "ris")),
            "main.tex" => Text(runId, wanted, "main.tex", "application/x-tex", downloads.Archive, MainTexOf(runId)),
            _ => new ApiReviewFile(runId, wanted, "archive.zip", "application/zip", downloads.Archive, null),
        };
        return file == null ? ApiProblems.NotReady(stage) : ApiResult<ApiReviewFile>.Ok(file);
    }

    private static ApiReviewFile? Text(Guid runId, string kind, string name, string mediaType, string address, string? content) =>
        content == null ? null : new ApiReviewFile(runId, kind, name, mediaType, address, content);

    /// <summary>A systematic review's main.tex, built in code as its archive holds it, without building the archive.</summary>
    private string? MainTexOf(Guid runId) => _engine.MainTexFromDisk(runId);
}

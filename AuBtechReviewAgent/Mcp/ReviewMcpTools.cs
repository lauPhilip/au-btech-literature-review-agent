using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AuBtechReviewAgent;

/// <summary>
/// The tools of the MCP server at /mcp. Each one calls <see cref="ReviewApi"/>, the same methods as the REST API, so
/// the checks, the limits and the caller's own key work the same way through both. The caller's MCP client sends its
/// Mistral key in the X-Mistral-Key header, as for the API. Answers are JSON text; a problem is a tool error with
/// the same plain-English title and detail the API sends.
/// </summary>
[McpServerToolType]
public sealed class ReviewMcpTools(ReviewApi reviews, IHttpContextAccessor http, IConfiguration configuration)
{
    private HttpContext Context => http.HttpContext ?? throw new McpException("The MCP server answers over HTTP only.");
    private string BaseUrl => SiteSeo.BaseUrl(configuration, Context.Request);

    [McpServerTool(Name = "start_literature_review"), Description(
        "Starts a systematic review (PRISMA 2020) with the caller's own Mistral key, sent in the X-Mistral-Key header. " +
        "Returns at once with the run id and its edit key; a review takes minutes, so poll get_review_status until the stage is Complete.")]
    public string StartLiteratureReview(
        [Description("The search words, the primary query.")] string searchWords,
        [Description("What a study must be about to be included.")] string inclusion,
        [Description("What the review should find out.")] string? objective = null,
        [Description("What leaves a study out.")] string? exclusion = null,
        [Description("Up to four extra search strings; left out, the model proposes them.")] string[]? searchStrings = null,
        [Description("First publication year, 0 for no limit.")] int yearFrom = 0,
        [Description("Last publication year, 0 for no limit.")] int yearTo = 0,
        [Description("Databases: arxiv, scopus, ieee, openalex, semanticscholar, crossref. Left out: every one the keys reach.")] string[]? databases = null,
        [Description("Up to four research questions.")] string[]? researchQuestions = null,
        [Description("The visual summary: concept-map, flowchart, table, bullets, custom or none.")] string? visualSummary = null,
        [Description("True to start even when a field reads like an instruction to the model; the phrases are recorded in the protocol.")] bool acceptFlaggedInput = false)
    {
        var context = Context;
        // The answer carries the edit key; McpSetup keeps every /mcp answer uncompressed.
        var body = new StartReviewBody
        {
            SearchWords = searchWords, Inclusion = inclusion, Objective = objective, Exclusion = exclusion,
            SearchStrings = searchStrings?.ToList(), YearFrom = yearFrom, YearTo = yearTo, Databases = databases?.ToList(),
            ResearchQuestions = researchQuestions?.ToList(), VisualSummary = visualSummary, AcceptFlaggedInput = acceptFlaggedInput,
        };
        return Answer(reviews.Start(body, ApiEndpoints.KeysFrom(context.Request),
            RunQuotaService.NormalizeClientAddress(context.Connection.RemoteIpAddress), BaseUrl));
    }

    [McpServerTool(Name = "get_review_status", ReadOnly = true), Description(
        "Where a review stands: its stage, the step of seven, the progress and the counts; when it is complete, the citation verdicts and the download addresses.")]
    public string GetReviewStatus([Description("The run id that start_literature_review returned.")] string id) =>
        Answer(reviews.Status(RunId(id), BaseUrl));

    [McpServerTool(Name = "get_review_report", ReadOnly = true), Description(
        "The finished report: the title, the abstract, the citation verdicts, every section with its text, and the flagged citations " +
        "(partly or not supported) with their sentences, reasons and quotes. Read from the run's files; nothing is generated.")]
    public string GetReviewReport([Description("The run id.")] string id)
    {
        var runId = RunId(id);
        var report = reviews.Report(runId);
        if (report.Problem != null) return Answer(report);
        var status = reviews.Status(runId, BaseUrl).Value;
        var r = report.Value!;
        var flagged = r.Citations.Where(c => c.Verdict is CitationSupportChecker.Partial or CitationSupportChecker.NotSupported).ToList();
        return Json(new { r.Id, r.Title, r.Abstract, Verdicts = status?.Verdicts, r.Sections, FlaggedCitations = flagged, r.AppVersion, r.ProtocolFingerprint });
    }

    [McpServerTool(Name = "get_review_file", ReadOnly = true), Description(
        "One file of a finished review: bibtex or ris (the included studies for a reference manager), main.tex (the paper as LaTeX), " +
        "or archive (the address of the zip with every file of the run).")]
    public string GetReviewFile(
        [Description("The run id.")] string id,
        [Description("bibtex, ris, main.tex or archive.")] string file) =>
        Answer(reviews.FileOf(RunId(id), file, BaseUrl));

    [McpServerTool(Name = "cancel_review", Destructive = true), Description(
        "Cancels a queued or running review, as the Cancel button on its page does. Needs the edit key that start_literature_review returned. " +
        "The review stops at its next safe point and ends in the stage Cancelled, with the reason in its ledger.")]
    public string CancelReview(
        [Description("The run id.")] string id,
        [Description("The edit key that start_literature_review returned.")] string editKey,
        [Description("Why it is cancelled, in one line; it is kept on file.")] string? reason = null) =>
        Answer(reviews.Cancel(RunId(id), editKey, reason, BaseUrl));

    /// <summary>A run id is a GUID; anything else never reaches a path.</summary>
    private static Guid RunId(string? id) =>
        Guid.TryParse(id, out var runId) ? runId : throw new McpException("The run id is not valid. Use the id that start_literature_review returned.");

    private static string Answer<T>(ApiResult<T> result) =>
        result.Problem is { } p ? throw new McpException($"{p.Title}. {p.Detail}{Details(p)}") : Json(result.Value);

    /// <summary>The list that goes with a problem (the fields to change, or the flagged phrases), as extra lines.</summary>
    private static string Details(ApiProblem problem) =>
        problem.Extensions == null ? "" : string.Concat(problem.Extensions.Values
            .OfType<System.Collections.Generic.IEnumerable<string>>().SelectMany(list => list).Select(line => "\n- " + line));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}

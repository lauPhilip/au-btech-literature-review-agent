using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The finished report as data (GET /api/v1/reviews/{id}/report), the OpenAPI description, and that the answer
/// holding the edit key is never compressed.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReviewApiReportTests
{
    private const string Key = "report-KEY-2b9c-traceable-api-test";

    [Fact]
    public async Task The_report_is_the_run_files_in_the_pages_order()
    {
        using var host = new ApiTestHost();
        var client = host.CreateClient();
        var id = await host.RunReviewAsync(client, Key);
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reviews/{id}");
        Assert.Equal(PrismaReviewEngine.StageComplete, status.GetProperty("stage").GetString());

        using var response = await client.GetAsync($"/api/v1/reviews/{id}/report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The text comes from prisma-report.json as it is; the fake run's text has no markers for the page to clean.
        string folder = host.Engine.Store.FolderOf(id);
        var onFile = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(onFile.TitleItem.Trim(), report.GetProperty("title").GetString());
        Assert.Equal(onFile.AbstractItem.Trim(), report.GetProperty("abstract").GetString());

        var outline = ReportOutline.Systematic(onFile).Select(e => e.Id).ToList();
        var sections = report.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(outline, sections.Select(s => s.GetProperty("id").GetString()!).ToList());
        var discussion = sections.Single(s => s.GetProperty("id").GetString() == ReportOutline.DiscussionId);
        Assert.Contains(onFile.DiscussionItem.Trim(), discussion.GetProperty("text").GetString());

        var citations = report.GetProperty("citations").EnumerateArray().ToList();
        Assert.Equal(status.GetProperty("verdicts").GetProperty("checked").GetInt32(), citations.Count);
        Assert.All(citations, c =>
        {
            Assert.False(string.IsNullOrEmpty(c.GetProperty("verdict").GetString()));
            Assert.True(c.GetProperty("reference").GetInt32() > 0);
            // A quote is only given when it was found word for word in the source.
            if (c.GetProperty("quote").ValueKind != JsonValueKind.Null) Assert.True(c.GetProperty("quoteVerified").GetBoolean());
        });
        Assert.Contains(citations, c => c.GetProperty("quote").ValueKind == JsonValueKind.String);

        var references = report.GetProperty("references").EnumerateArray().ToList();
        Assert.Equal(status.GetProperty("counts").GetProperty("included").GetInt32(), references.Count);
        Assert.Contains(references, r => r.GetProperty("link").GetString()?.StartsWith("https://doi.org/") == true);
        Assert.Equal(references.Count, report.GetProperty("prisma").GetProperty("included").GetInt32());

        var state = host.Engine.LoadState(id)!;
        Assert.Equal(state.RunSettings!.AppVersion, report.GetProperty("appVersion").GetString());
        Assert.False(string.IsNullOrEmpty(report.GetProperty("protocolFingerprint").GetString()));
    }

    [Fact]
    public async Task A_report_that_is_not_ready_or_not_there_is_a_problem()
    {
        using var host = new ApiTestHost();
        var client = host.CreateClient();
        var queued = Guid.NewGuid();
        host.Engine.ClaimRun(queued); // the folder of a run that has not started yet

        using var notReady = await client.GetAsync($"/api/v1/reviews/{queued}/report");
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Equal("application/problem+json", notReady.Content.Headers.ContentType?.MediaType);
        using var missing = await client.GetAsync($"/api/v1/reviews/{Guid.NewGuid()}/report");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_OpenAPI_description_covers_the_API_and_only_the_API()
    {
        using var host = new ApiTestHost();
        var document = await host.CreateClient().GetFromJsonAsync<JsonElement>(ApiEndpoints.OpenApiPath);
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/api/v1/reviews", paths);
        Assert.Contains("/api/v1/reviews/{id}", paths);
        Assert.Contains("/api/v1/reviews/{id}/report", paths);
        Assert.DoesNotContain("/health", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/", p));

        using var off = new ApiTestHost(enabled: false);
        using var response = await off.CreateClient().GetAsync(ApiEndpoints.OpenApiPath);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_answer_with_the_edit_key_is_never_compressed()
    {
        using var host = new ApiTestHost();
        // Over HTTPS, as on the server: the opt-out is for HTTPS answers, where BREACH applies.
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, br");

        using var started = await ReviewApiTests.PostAsync(client, ReviewApiTests.GoodBody(), Key);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Empty(started.Content.Headers.ContentEncoding);
        var id = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Compression is on for other answers, so the start is left out on purpose and not by accident.
        using var status = await client.GetAsync($"/api/v1/reviews/{id}");
        Assert.NotEmpty(status.Content.Headers.ContentEncoding);
        await host.WaitForRunAsync(id, TimeSpan.FromSeconds(90));
    }
}

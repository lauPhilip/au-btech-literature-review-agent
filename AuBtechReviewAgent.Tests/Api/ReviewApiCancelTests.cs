using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Cancelling a review through the API (DELETE /api/v1/reviews/{id} with X-Edit-Key) and through the MCP tool
/// cancel_review (issue #232). The fake model waits at a gate, so the review is caught while it runs.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReviewApiCancelTests
{
    private const string Key = "cancel-KEY-7f3a-traceable-test-do-not-store";

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id, string? editKey, string? reason = null)
    {
        string address = $"/api/v1/reviews/{id}" + (reason == null ? "" : "?reason=" + Uri.EscapeDataString(reason));
        using var message = new HttpRequestMessage(HttpMethod.Delete, address);
        if (editKey != null) message.Headers.Add(ApiEndpoints.EditKeyHeader, editKey);
        return await client.SendAsync(message);
    }

    private static async Task<string> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    [Fact]
    public async Task A_running_review_is_cancelled_with_its_edit_key_only()
    {
        using var gate = new ManualResetEventSlim(false);
        using var host = new ApiTestHost(gate: gate);
        var client = host.CreateClient();
        try
        {
            using var started = await ReviewApiTests.PostAsync(client, ReviewApiTests.GoodBody(), Key);
            var answer = await started.Content.ReadFromJsonAsync<JsonElement>();
            var id = answer.GetProperty("id").GetGuid();
            string editKey = answer.GetProperty("editKey").GetString()!;
            Assert.True(host.ModelEntered.Wait(TimeSpan.FromSeconds(30)));

            using (var missing = await DeleteAsync(client, id, null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
                Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
                Assert.Equal("The edit key is missing", await TitleOf(missing));
            }
            using (var wrong = await DeleteAsync(client, id, "not-the-edit-key"))
                Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            using (var unknown = await DeleteAsync(client, Guid.NewGuid(), editKey))
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.True(host.Engine.IsRunActive(id), "a refused cancel leaves the run alone");

            using (var cancelled = await DeleteAsync(client, id, editKey, "Wrong question"))
            {
                Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
                var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("Wrong question", body.GetProperty("reason").GetString());
                Assert.EndsWith($"/api/v1/reviews/{id}", cancelled.Headers.Location?.ToString());
            }
            gate.Set();
            await host.WaitForRunAsync(id, TimeSpan.FromSeconds(30));

            var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reviews/{id}");
            Assert.Equal(PrismaReviewEngine.StageCancelled, status.GetProperty("stage").GetString());
            Assert.Contains("Wrong question", status.GetProperty("failure").GetString());
            Assert.Equal("Wrong question", host.Engine.LoadState(id)!.CancelReason);

            using var again = await DeleteAsync(client, id, editKey);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("already ended", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

            foreach (var file in Directory.GetFiles(host.Engine.Store.FolderOf(id), "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(Key, File.ReadAllText(file), StringComparison.Ordinal);
            Assert.DoesNotContain(Key, host.Logs.All);
            Assert.DoesNotContain(editKey, host.Logs.All);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task The_MCP_tool_cancels_a_running_review()
    {
        using var gate = new ManualResetEventSlim(false);
        using var host = new ApiTestHost(gate: gate);
        try
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost" + McpSetup.Path),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { [ApiEndpoints.MistralKeyHeader] = Key },
            };
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(options, host.CreateClient()));
            var started = await mcp.CallToolAsync("start_literature_review", new Dictionary<string, object?>
            {
                ["searchWords"] = "agent loops", ["inclusion"] = "Agent architecture", ["databases"] = new[] { "arxiv" }, ["visualSummary"] = "none",
            });
            var answer = JsonDocument.Parse(Text(started)).RootElement;
            string id = answer.GetProperty("id").GetString()!, editKey = answer.GetProperty("editKey").GetString()!;
            Assert.True(host.ModelEntered.Wait(TimeSpan.FromSeconds(30)));

            var wrong = await mcp.CallToolAsync("cancel_review", new Dictionary<string, object?> { ["id"] = id, ["editKey"] = "not-the-edit-key" });
            Assert.True(wrong.IsError);
            Assert.Contains("edit key does not fit", Text(wrong));

            var cancelled = await mcp.CallToolAsync("cancel_review", new Dictionary<string, object?> { ["id"] = id, ["editKey"] = editKey });
            Assert.NotEqual(true, cancelled.IsError);
            Assert.Equal(PrismaReviewEngine.NoCancelReason, JsonDocument.Parse(Text(cancelled)).RootElement.GetProperty("reason").GetString());
            gate.Set();
            await host.WaitForRunAsync(Guid.Parse(id), TimeSpan.FromSeconds(30));
            Assert.Equal(PrismaReviewEngine.StageCancelled, host.Engine.LoadState(Guid.Parse(id))!.Stats.ProcessingStage);
            Assert.DoesNotContain(editKey, host.Logs.All); // a tool's arguments are not logged
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task With_the_switch_off_there_is_no_cancel()
    {
        using var host = new ApiTestHost(enabled: false);
        using var response = await DeleteAsync(host.CreateClient(), Guid.NewGuid(), "any-key");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

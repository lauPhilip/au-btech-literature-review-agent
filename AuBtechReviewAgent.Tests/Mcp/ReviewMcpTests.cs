using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The MCP server through the SDK's own HTTP client, against the real app with the scripted fake model: the five
/// tools, a whole review started and read through them, the caller's key in a header and never on file, and errors.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReviewMcpTests
{
    private const string Key = "mcp-KEY-41d0-traceable-test-do-not-store";

    private static async Task<McpClient> ConnectAsync(ApiTestHost host, string? key = Key)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost" + McpSetup.Path),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = key == null ? new Dictionary<string, string>() : new Dictionary<string, string> { [ApiEndpoints.MistralKeyHeader] = key },
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options, host.CreateClient()));
    }

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, text);
    }

    [Fact]
    public async Task The_server_offers_the_five_tools()
    {
        using var host = new ApiTestHost();
        await using var client = await ConnectAsync(host);
        var names = (await client.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "cancel_review", "get_review_file", "get_review_report", "get_review_status", "start_literature_review" }, names);
    }

    [Fact]
    public async Task A_review_runs_through_the_tools_with_the_callers_key_only()
    {
        using var host = new ApiTestHost();
        await using var client = await ConnectAsync(host);

        var (error, text) = await CallAsync(client, "start_literature_review", new()
        {
            ["searchWords"] = "agent loops", ["inclusion"] = "Agent architecture", ["exclusion"] = "Agronomy",
            ["objective"] = "Map agent loop safety", ["databases"] = new[] { "arxiv" }, ["visualSummary"] = "none",
        });
        Assert.False(error, text);
        var id = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();

        string stage = "";
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until && !PrismaReviewEngine.IsFinishedStage(stage))
        {
            await Task.Delay(ApiTestHost.PollInterval);
            (error, text) = await CallAsync(client, "get_review_status", new() { ["id"] = id.ToString() });
            Assert.False(error, text);
            stage = JsonDocument.Parse(text).RootElement.GetProperty("stage").GetString()!;
        }
        await host.WaitForRunAsync(id, TimeSpan.FromSeconds(30));
        Assert.Equal(PrismaReviewEngine.StageComplete, stage);

        (error, text) = await CallAsync(client, "get_review_report", new() { ["id"] = id.ToString() });
        Assert.False(error, text);
        var report = JsonDocument.Parse(text).RootElement;
        Assert.False(string.IsNullOrEmpty(report.GetProperty("title").GetString()));
        Assert.NotEmpty(report.GetProperty("sections").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, report.GetProperty("flaggedCitations").ValueKind);
        Assert.True(report.GetProperty("verdicts").GetProperty("checked").GetInt32() > 0);

        foreach (var (kind, expected) in new[] { ("bibtex", "@"), ("ris", "TY"), ("main.tex", "\\documentclass") })
        {
            (error, text) = await CallAsync(client, "get_review_file", new() { ["id"] = id.ToString(), ["file"] = kind });
            Assert.False(error, text);
            Assert.Contains(expected, JsonDocument.Parse(text).RootElement.GetProperty("content").GetString());
        }
        (error, text) = await CallAsync(client, "get_review_file", new() { ["id"] = id.ToString(), ["file"] = "archive" });
        Assert.False(error, text);
        Assert.EndsWith($"/api/workspace/{id}/archive", JsonDocument.Parse(text).RootElement.GetProperty("address").GetString());

        Assert.All(host.KeysSeenByModel, key => Assert.Equal(Key, key));
        foreach (var file in Directory.GetFiles(host.Engine.Store.FolderOf(id), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Key, File.ReadAllText(file), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, host.Logs.All);
    }

    [Fact]
    public async Task Without_a_key_or_with_a_bad_id_the_tools_answer_with_an_error()
    {
        using var host = new ApiTestHost();
        await using var client = await ConnectAsync(host, key: null);
        var (error, text) = await CallAsync(client, "start_literature_review", new() { ["searchWords"] = "agent loops", ["inclusion"] = "Agents" });
        Assert.True(error);
        Assert.Contains(ApiEndpoints.MistralKeyHeader, text);
        Assert.Empty(host.KeysSeenByModel);

        (error, text) = await CallAsync(client, "get_review_status", new() { ["id"] = "../../etc" });
        Assert.True(error);
        Assert.Contains("run id is not valid", text);
        (error, _) = await CallAsync(client, "get_review_file", new() { ["id"] = Guid.NewGuid().ToString(), ["file"] = "secrets.json" });
        Assert.True(error);
    }

    [Fact]
    public async Task The_MCP_answer_with_the_edit_key_is_never_compressed()
    {
        using var host = new ApiTestHost();
        // Over HTTPS, as on the server: the opt-out is for HTTPS answers, where BREACH applies.
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, br");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        client.DefaultRequestHeaders.Add(ApiEndpoints.MistralKeyHeader, Key);
        const string call = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"start_literature_review",
             "arguments":{"searchWords":"agent loops","inclusion":"Agent architecture","databases":["arxiv"],"visualSummary":"none"}}}
            """;
        using var body = new StringContent(call, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(McpSetup.Path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The SDK may mark its event stream "identity"; anything else would be a compression.
        Assert.All(response.Content.Headers.ContentEncoding, encoding => Assert.Equal("identity", encoding));
        string text = await response.Content.ReadAsStringAsync();
        Assert.Contains("editKey", text);
        var id = Guid.Parse(System.Text.RegularExpressions.Regex.Match(text, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}").Value);
        await host.WaitForRunAsync(id, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task With_the_switch_off_there_is_no_MCP_server()
    {
        using var host = new ApiTestHost(enabled: false);
        using var body = new StringContent("{}");
        using var response = await host.CreateClient().PostAsync(McpSetup.Path, body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

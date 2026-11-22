using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent.Components.Pages;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The API and MCP documentation page (/docs/api, issue #314), through the real app: it exists only with the API
/// switched on, every paragraph is plain English (ReadingEase of 60 or more), and every example on it is the real
/// request and answer of the running API, so the page cannot drift from it.
/// </summary>
[Collection(ApiCollection.Name)]
public partial class ApiDocsPageTests(ITestOutputHelper output)
{
    private const string Key = "docs-KEY-7f3a-traceable-test-do-not-store";

    [Fact]
    public async Task With_the_switch_off_the_page_is_not_found_and_nothing_links_to_it()
    {
        foreach (var host in new[] { new ApiTestHost(enabled: false), new ApiTestHost(enabled: null, environment: "Production") })
        {
            using (host)
            {
                var client = host.CreateClient();
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ApiSwitch.DocsPath)).StatusCode);
                Assert.DoesNotContain(ApiSwitch.DocsPath, await client.GetStringAsync("/about"));
                Assert.DoesNotContain(ApiSwitch.DocsPath, await client.GetStringAsync("/sitemap.xml"));
            }
        }
    }

    [Fact]
    public async Task With_the_switch_on_the_page_is_listed_and_the_OpenAPI_description_links_to_it()
    {
        using var host = new ApiTestHost();
        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ApiSwitch.DocsPath)).StatusCode);
        Assert.Contains($"href=\"{ApiSwitch.DocsPath}\"", await client.GetStringAsync("/about"));

        var openApi = await client.GetFromJsonAsync<JsonElement>(ApiEndpoints.OpenApiPath);
        Assert.EndsWith(ApiSwitch.DocsPath, openApi.GetProperty("externalDocs").GetProperty("url").GetString());
        string page = await client.GetStringAsync(ApiSwitch.DocsPath);
        Assert.Contains($"href=\"{ApiEndpoints.OpenApiPath}\"", page);
        Assert.Contains("docs/wiki/09-api-and-mcp.md", page);
    }

    [Fact]
    public async Task Every_paragraph_scores_at_least_60_on_the_reading_ease()
    {
        using var host = new ApiTestHost();
        string page = await host.CreateClient().GetStringAsync(ApiSwitch.DocsPath);
        string article = page[page.IndexOf("<article", StringComparison.Ordinal)..page.IndexOf("</article>", StringComparison.Ordinal)];

        // Prose is every paragraph, table cell and quote. Code, field names and headers are names, not words.
        var scores = Prose().Matches(article)
            .Select(m => WebUtility.HtmlDecode(Tag().Replace(CodeElement().Replace(m.Groups[2].Value, " "), " ")).Trim())
            .Where(text => text.Length > 0)
            .Select(text => (Text: text, Score: ReadingEase.Score(text)))
            .Where(x => x.Score != null)
            .ToList();

        Assert.True(scores.Count > 30, $"only {scores.Count} paragraphs were found");
        var lowest = scores.MinBy(x => x.Score)!;
        output.WriteLine($"{scores.Count} paragraphs; the lowest scores {lowest.Score:F1}: {lowest.Text}");
        var hard = scores.Where(x => x.Score < ReadingEase.Minimum).Select(x => $"{x.Score:F1}: {x.Text}").ToList();
        Assert.True(hard.Count == 0, "Paragraphs below 60:\n" + string.Join("\n", hard));
    }

    [Fact]
    public async Task The_page_shows_the_examples_and_names_only_real_fields_and_tools()
    {
        using var host = new ApiTestHost();
        string page = WebUtility.HtmlDecode(await host.CreateClient().GetStringAsync(ApiSwitch.DocsPath));
        foreach (string example in new[]
        {
            ApiDocExamples.StartCurl("http://localhost"), ApiDocExamples.Started("http://localhost"), ApiDocExamples.Status("http://localhost"),
            ApiDocExamples.Report, ApiDocExamples.Cancelled("http://localhost"), ApiDocExamples.Problem(), ApiDocExamples.McpConfig("http://localhost"),
        })
        {
            Assert.Contains(example, page);
        }

        var properties = typeof(StartReviewBody).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();
        var listed = ApiDocsStart.Fields.SelectMany(g => g.Fields).SelectMany(f => f.Name.Split(", ")).ToList();
        Assert.Equal(properties.Order(), listed.Order());
        Assert.All(ApiDocsStart.Fields, g => Assert.InRange(g.Fields.Length, 1, 7));

        var tools = typeof(ReviewMcpTools).GetMethods().Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>();
        Assert.Equal(tools.Order(), ApiDocsAssistant.Tools.Select(t => t.Tool).Order());
    }

    [Fact]
    public async Task The_example_requests_are_accepted_and_the_answers_have_the_real_shape()
    {
        using var host = new ApiTestHost();
        var client = host.CreateClient();

        using var started = await PostStartBodyAsync(client, Key);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var startAnswer = await started.Content.ReadFromJsonAsync<JsonElement>();
        AssertSameShape(ApiDocExamples.Started("http://localhost"), startAnswer);

        var id = startAnswer.GetProperty("id").GetGuid();
        var until = DateTime.UtcNow.AddSeconds(90);
        JsonElement status;
        do
        {
            await Task.Delay(500);
            status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reviews/{id}");
        }
        while (!PrismaReviewEngine.IsFinishedStage(status.GetProperty("stage").GetString()) && DateTime.UtcNow < until);
        await host.WaitForRunAsync(id, TimeSpan.FromSeconds(30));
        status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reviews/{id}");
        Assert.Equal(PrismaReviewEngine.StageComplete, status.GetProperty("stage").GetString());
        AssertSameShape(ApiDocExamples.Status("http://localhost"), status);
        AssertSameShape(ApiDocExamples.Report, await client.GetFromJsonAsync<JsonElement>($"/api/v1/reviews/{id}/report"));

        using var noKey = await PostStartBodyAsync(client, null);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        var problem = await noKey.Content.ReadFromJsonAsync<JsonElement>();
        AssertSameShape(ApiDocExamples.Problem(), problem);
        var shown = JsonDocument.Parse(ApiDocExamples.Problem()).RootElement;
        foreach (string name in new[] { "type", "title", "detail" })
            Assert.Equal(shown.GetProperty(name).GetString(), problem.GetProperty(name).GetString());
    }

    [Fact]
    public async Task The_example_cancel_is_accepted_and_its_answer_has_the_real_shape()
    {
        using var gate = new ManualResetEventSlim(false);
        using var host = new ApiTestHost(gate: gate);
        var client = host.CreateClient();
        try
        {
            using var started = await PostStartBodyAsync(client, Key);
            var answer = await started.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(host.ModelEntered.Wait(TimeSpan.FromSeconds(30)));

            // The example's own address and header, with this run's id and edit key in place of the made-up ones.
            string curl = ApiDocExamples.CancelCurl("").Replace(ApiDocExamples.RunId, answer.GetProperty("id").GetString());
            string address = CurlAddress().Match(curl).Groups[1].Value;
            using var message = new HttpRequestMessage(HttpMethod.Delete, address);
            message.Headers.Add(ApiEndpoints.EditKeyHeader, answer.GetProperty("editKey").GetString());
            Assert.Contains($"-H \"{ApiEndpoints.EditKeyHeader}: {ApiDocExamples.EditKey}\"", ApiDocExamples.CancelCurl(""));

            using var cancelled = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
            var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(ApiDocExamples.Reason, body.GetProperty("reason").GetString());
            AssertSameShape(ApiDocExamples.Cancelled("http://localhost"), body);
        }
        finally { gate.Set(); }
    }

    /// <summary>Sends the start exactly as the page's curl example does: its body, with the key in X-Mistral-Key.</summary>
    private static async Task<HttpResponseMessage> PostStartBodyAsync(HttpClient client, string? key)
    {
        Assert.Contains($"-H \"{ApiEndpoints.MistralKeyHeader}: $MISTRAL_API_KEY\"", ApiDocExamples.StartCurl(""));
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reviews")
        {
            Content = new StringContent(ApiDocExamples.StartBody, System.Text.Encoding.UTF8, "application/json"),
        };
        if (key != null) message.Headers.Add(ApiEndpoints.MistralKeyHeader, key);
        return await client.SendAsync(message);
    }

    /// <summary>The example has exactly the real answer's property names, at every level of nesting.</summary>
    private static void AssertSameShape(string example, JsonElement real)
    {
        var differences = new List<string>();
        Compare(JsonDocument.Parse(example).RootElement, real, "$", differences);
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static void Compare(JsonElement example, JsonElement real, string path, List<string> differences)
    {
        if (example.ValueKind == JsonValueKind.Null || real.ValueKind == JsonValueKind.Null) return; // a value that may be null
        if (example.ValueKind == JsonValueKind.Object && real.ValueKind == JsonValueKind.Object)
        {
            var shown = example.EnumerateObject().Select(p => p.Name).ToHashSet();
            var sent = real.EnumerateObject().Select(p => p.Name).ToHashSet();
            differences.AddRange(sent.Except(shown).Select(name => $"{path}.{name} is in the answer but not in the example"));
            differences.AddRange(shown.Except(sent).Select(name => $"{path}.{name} is in the example but not in the answer"));
            foreach (string name in shown.Intersect(sent))
                Compare(example.GetProperty(name), real.GetProperty(name), $"{path}.{name}", differences);
        }
        else if (example.ValueKind == JsonValueKind.Array && real.ValueKind == JsonValueKind.Array)
        {
            if (example.GetArrayLength() > 0 && real.GetArrayLength() > 0) Compare(example[0], real[0], path + "[0]", differences);
        }
        else if (Kind(example) != Kind(real))
        {
            differences.Add($"{path} is {example.ValueKind} in the example but {real.ValueKind} in the answer");
        }
    }

    private static JsonValueKind Kind(JsonElement e) => e.ValueKind == JsonValueKind.False ? JsonValueKind.True : e.ValueKind;

    [GeneratedRegex(@"<(p|td|blockquote)\b[^>]*>(.*?)</\1>", RegexOptions.Singleline)] private static partial Regex Prose();
    [GeneratedRegex(@"<code\b[^>]*>.*?</code>", RegexOptions.Singleline)] private static partial Regex CodeElement();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();
    [GeneratedRegex("\"([^\"]+)\"")] private static partial Regex CurlAddress();
}

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>/metrics/runs.csv, /metrics/runs.json and /metrics/stages.csv served by the whole app (issue #416).</summary>
[Collection(ApiCollection.Name)]
public sealed class MetricsDownloadsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "metrics-dl-" + Guid.NewGuid().ToString("N"));

    private sealed class Host(string folder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("MISTRAL_API_KEY", "server-key-for-the-metrics-test");
            builder.UseSetting("Metrics:Folder", folder);
        }
    }

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task TheFilesServeEveryFinishedRunAsAnAttachment()
    {
        var store = new RunMetricsStore(new MetricsOptions { Folder = _root }, _root);
        store.Append(new RunMetrics { RunId = "aaaaaaaa11112222333344445555aaaa", CompletedUtc = "2026-11-20T10:00:00Z", ReviewType = "systematic", QueryHash = "secrethash01" });
        store.Append(new RunMetrics { RunId = "bbbbbbbb11112222333344445555bbbb", CompletedUtc = "2026-11-21T10:00:00Z", ReviewType = "multivocal", Settings = new() { ["Module"] = "multivocal" } });
        using var host = new Host(_root);
        using var client = host.CreateClient();

        var csv = await client.GetAsync(MetricsDownloads.RunsCsv);
        csv.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", csv.Content.Headers.ContentDisposition?.DispositionType ?? csv.Headers.GetValues("Content-Disposition").First().Split(';')[0]);
        var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("run,review_type,", lines[0]);
        Assert.StartsWith("aaaaaaaa,systematic,", lines[1]);
        Assert.StartsWith("bbbbbbbb,multivocal,", lines[2]); // the grey literature review is on, so its runs are in
        Assert.DoesNotContain("secrethash01", string.Join("\n", lines));

        using var json = JsonDocument.Parse(await client.GetStringAsync(MetricsDownloads.RunsJson));
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.StartsWith("run,review_type,tool_version,group", await client.GetStringAsync(MetricsDownloads.StagesCsv));
    }
}

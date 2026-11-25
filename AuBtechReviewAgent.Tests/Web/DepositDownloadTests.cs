using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>GET /api/workspace/{id}/deposit through the real app: the archive to share on Zenodo or OSF.</summary>
[Collection(ApiCollection.Name)]
public class DepositDownloadTests
{
    private const string Key = "deposit-KEY-4d1e-traceable-api-test";

    [Fact]
    public async Task AFinishedRunGivesAnArchiveToShareThatChecksOutAndIsAnswered304WhenUnchanged()
    {
        using var host = new ApiTestHost();
        var client = host.CreateClient();
        using var started = await ReviewApiTests.PostAsync(client, ReviewApiTests.GoodBody(), Key);
        var id = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await host.WaitForRunAsync(id, TimeSpan.FromSeconds(90));

        using var response = await client.GetAsync($"/api/workspace/{id}/deposit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("PRISMA_Deposit_", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        byte[] zip = await response.Content.ReadAsByteArrayAsync();
        Assert.True(RunManifest.CheckArchive(new MemoryStream(zip)).Unchanged);
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("main.tex", names);
            Assert.Contains(DepositArchive.RecordFile, names);
            Assert.DoesNotContain("llm-calls.json", names);
        }
        // The temporary copy is gone once the download has been sent.
        Assert.Empty(Directory.GetFiles(host.Engine.Store.FolderOf(id), "deposit-*"));

        var again = new HttpRequestMessage(HttpMethod.Get, $"/api/workspace/{id}/deposit");
        again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var unchanged = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

        using var unknown = await client.GetAsync($"/api/workspace/{Guid.NewGuid()}/deposit");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}

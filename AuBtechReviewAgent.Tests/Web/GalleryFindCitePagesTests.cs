using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The gallery's search box, the citation and self-check lines, and "Cite this report", served by the whole app.</summary>
[Collection(ApiCollection.Name)]
public sealed class GalleryFindCitePagesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-find-pages-" + Guid.NewGuid().ToString("N"));
    private string GalleryFolder => Path.Join(_root, "gallery");

    private sealed class Host(string folder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("MISTRAL_API_KEY", "server-key-for-the-gallery-test");
            builder.UseSetting("Gallery:Folder", folder);
            builder.UseSetting("Site:PublicUrl", "https://reviews.example.org/");
        }
    }

    public void Dispose() => TestFolders.TryDelete(_root);

    private async Task<GalleryEntry> AddedAsync(string topic, string title, bool selfCheck = false)
    {
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var gallery = new GalleryStore(new GalleryOptions { Folder = GalleryFolder }, _root, runs);
        var (id, key) = await GalleryStoreTests.FinishedRunAsync(runs, topic, new[] { $"What is known about {topic}?" }, title);
        if (selfCheck)
            File.WriteAllText(Path.Join(runs.FolderOf(id), PaperSelfCheckFile.FileName), "{\"Passed\":8,\"Notes\":2,\"Faults\":1}");
        return (await gallery.AddAsync(id, key, consent: true)).Entry!;
    }

    [Fact]
    public async Task TheSearchBoxFiltersOnTheServerAndKeepsTheTypeAndOrder()
    {
        var schools = await AddedAsync("AI in schools", "AI tutors in schools", selfCheck: true);
        var hospitals = await AddedAsync("AI in hospitals", "Triage with AI in hospitals");
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        string all = await client.GetStringAsync("/gallery");
        Assert.Contains("data-gallery-search", all);
        Assert.Contains($"/gallery/{schools.Id}", all);
        Assert.Contains($"/gallery/{hospitals.Id}", all);
        Assert.Contains("Self-check: 8 checks passed, 2 notes, 1 fault", all);

        string found = await client.GetStringAsync("/gallery?q=tutors&type=systematic&sort=title");
        Assert.Contains($"/gallery/{schools.Id}", found);
        Assert.DoesNotContain($"/gallery/{hospitals.Id}", found);
        Assert.Contains("1 report matches", found);
        Assert.Contains("name=\"type\" value=\"systematic\"", found);
        Assert.Contains("href=\"/gallery?q=tutors&amp;type=systematic\"", found);

        string none = await client.GetStringAsync("/gallery?q=%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        Assert.Contains("No report matches", none);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", none);
        Assert.DoesNotContain("<script>alert(1)", none);
    }

    [Fact]
    public async Task AnEntryOffersAnApaAndBibTexCitationOfItsOwnVersion()
    {
        var entry = await AddedAsync("AI in schools", "AI & tutors in schools", selfCheck: true);
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        var page = await client.GetAsync($"/gallery/{entry.Id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Cite this report", html);
        Assert.Contains("artificial researcher. (", html);
        Assert.Contains($"https://reviews.example.org/gallery/{entry.Id}/v/1", html);
        Assert.Contains($"@misc{{traceableai_{entry.ShortRunId},", html);
        Assert.Contains(@"AI \&amp; tutors in schools", html);
        Assert.Contains("not peer reviewed", html);
        Assert.Contains("Self-check: 8 checks passed, 2 notes, 1 fault", html);
    }
}

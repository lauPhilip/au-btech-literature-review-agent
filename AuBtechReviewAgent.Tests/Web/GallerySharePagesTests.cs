using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The rest of issue #542, served by the whole app: the year and version filters, the preview a shared entry link
/// shows, the full self-check table on an entry's page, and the gallery in the sitemap.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class GallerySharePagesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-share-pages-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Adds a finished run, then sets the year it finished and the tool's version in the entry, as an older run would have them.</summary>
    private async Task<GalleryEntry> AddedAsync(string topic, string title, int year, string version, string? question = null, PaperSelfCheckFile? selfCheck = null)
    {
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var gallery = new GalleryStore(new GalleryOptions { Folder = GalleryFolder }, _root, runs);
        var (id, key) = await GalleryStoreTests.FinishedRunAsync(runs, topic, new[] { question ?? $"What is known about {topic}?" }, title);
        if (selfCheck != null)
            File.WriteAllText(Path.Join(runs.FolderOf(id), PaperSelfCheckFile.FileName), JsonSerializer.Serialize(selfCheck));
        var entry = (await gallery.AddAsync(id, key, consent: true)).Entry!;

        string file = Path.Join(GalleryFolder, entry.Id, GalleryEntry.FileName);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json[nameof(GalleryEntry.CompletedUtc)] = new DateTime(year, 5, 4, 10, 0, 0, DateTimeKind.Utc);
        json[nameof(GalleryEntry.ToolVersion)] = version;
        File.WriteAllText(file, json.ToJsonString());
        return entry;
    }

    [Fact]
    public async Task TheYearAndVersionFiltersNarrowTheListOnTheServerAndStayInEveryLink()
    {
        var old = await AddedAsync("AI in schools", "AI tutors in schools", 2025, "0.6.0+abcdef1234567");
        var recent = await AddedAsync("AI in hospitals", "Triage with AI in hospitals", 2026, "0.7.1-alpha.0.3+abcdef1234567");
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        string all = await client.GetStringAsync("/gallery");
        Assert.Contains("<option value=\"2026\">2026</option>", all);
        Assert.Contains("<option value=\"0.7\">TraceableAI 0.7</option>", all);
        Assert.Contains("<option value=\"0.6\">TraceableAI 0.6</option>", all);

        string by2026 = await client.GetStringAsync("/gallery?year=2026&sort=title");
        Assert.Contains($"/gallery/{recent.Id}", by2026);
        Assert.DoesNotContain($"/gallery/{old.Id}", by2026);
        Assert.Contains("1 report matches: finished in 2026.", by2026);
        Assert.Contains("<option value=\"2026\" selected", by2026);
        Assert.Contains("href=\"/gallery?year=2026&amp;type=systematic&amp;sort=title\"", by2026);

        string byVersion = await client.GetStringAsync("/gallery?version=0.6");
        Assert.Contains($"/gallery/{old.Id}", byVersion);
        Assert.DoesNotContain($"/gallery/{recent.Id}", byVersion);

        // A year or version no report has, or a malformed one, is ignored rather than echoed.
        string odd = await client.GetStringAsync("/gallery?year=1990&version=%22%3E%3Cscript%3E");
        Assert.Contains($"/gallery/{old.Id}", odd);
        Assert.Contains($"/gallery/{recent.Id}", odd);
        Assert.DoesNotContain("<script>", odd);
    }

    [Fact]
    public async Task ASharedEntryLinkPreviewsAsAnArticleWithItsTitleAndQuestionEscaped()
    {
        var entry = await AddedAsync("AI in schools", "Tutors \"<b>\" in schools", 2026, "0.7.0", question: "Do <i>AI</i> tutors help?");
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        string html = await client.GetStringAsync($"/gallery/{entry.Id}");
        Assert.Contains("<meta property=\"og:type\" content=\"article\"", html);
        Assert.Contains("<meta property=\"article:published_time\" content=\"", html);
        Assert.Contains("<meta property=\"og:title\" content=\"Tutors &quot;&lt;b&gt;&quot; in schools", html);
        Assert.Contains("<meta name=\"twitter:description\" content=\"An example systematic literature review made with TraceableAI, with every citation checked against its source. Do &lt;i&gt;AI&lt;/i&gt; tutors help?\"", html);
        Assert.Contains("<meta property=\"og:image\" content=\"https://reviews.example.org/img/og-card.png\"", html);
        Assert.DoesNotContain("<i>AI</i>", html);

        Assert.Contains("<meta property=\"og:type\" content=\"website\"", await client.GetStringAsync("/gallery"));
    }

    [Fact]
    public async Task AnEntryPageShowsTheWholeSelfCheckTable()
    {
        var check = new PaperSelfCheckFile
        {
            Passed = 1, Faults = 1,
            Checks =
            {
                new PaperCheck { Number = 1, Id = "answers", Title = "Every question has an answer", Outcome = CheckOutcome.Pass, Summary = "All 2 questions are answered." },
                new PaperCheck { Number = 2, Id = "counts", Title = "Counts agree", Outcome = CheckOutcome.Fault, Summary = "The abstract says 8 studies; the flow diagram says 7." },
            },
        };
        var entry = await AddedAsync("AI in schools", "AI tutors in schools", 2026, "0.7.0", selfCheck: check);
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        string html = await client.GetStringAsync($"/gallery/{entry.Id}");
        Assert.Contains($"id=\"{ReportOutline.SelfCheckId}\"", html);
        Assert.Contains("Counts agree", html);
        Assert.Contains("The abstract says 8 studies; the flow diagram says 7.", html);
    }

    [Fact]
    public async Task TheGalleryAndItsEntriesAreInTheSitemap()
    {
        var entry = await AddedAsync("AI in schools", "AI tutors in schools", 2026, "0.7.0");
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        string sitemap = await client.GetStringAsync("/sitemap.xml");
        Assert.Contains("<loc>https://reviews.example.org/gallery</loc>", sitemap);
        Assert.Contains($"<loc>https://reviews.example.org/gallery/{entry.Id}</loc>", sitemap);
        Assert.DoesNotContain($"/gallery/{entry.Id}/v/", sitemap);
    }
}

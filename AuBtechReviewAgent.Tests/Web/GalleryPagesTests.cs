using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The gallery pages served by the whole app: the catalogue, one report as it prints, hidden entries, and the print view's styles.</summary>
[Collection(ApiCollection.Name)]
public sealed class GalleryPagesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-pages-" + Guid.NewGuid().ToString("N"));
    private string GalleryFolder => Path.Join(_root, "gallery");

    private sealed class Host(string folder, string hidden = "") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("MISTRAL_API_KEY", "server-key-for-the-gallery-test");
            builder.UseSetting("Gallery:Folder", folder);
            if (hidden.Length > 0) builder.UseSetting("Gallery:HiddenIds:0", hidden);
        }
    }

    public void Dispose() => TestFolders.TryDelete(_root);

    private async Task<GalleryEntry> AddedEntryAsync(string title)
    {
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var gallery = new GalleryStore(new GalleryOptions { Folder = GalleryFolder }, _root, runs);
        var (id, key) = await GalleryStoreTests.FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used in schools?" }, title);
        return (await gallery.AddAsync(id, key, consent: true)).Entry!;
    }

    [Fact]
    public async Task TheCatalogueListsAnEntryAsTextWithTheSiteCsp()
    {
        var entry = await AddedEntryAsync("AI <script>alert(1)</script> in schools");
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        var page = await client.GetAsync("/gallery");
        page.EnsureSuccessStatusCode();
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains($"/gallery/{entry.Id}", html);
        Assert.Contains("AI &lt;script&gt;alert(1)&lt;/script&gt; in schools", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("How is AI used in schools?", html);
        Assert.Contains("Teacher workload", html);
        Assert.Contains("Each review is in the gallery once.", html);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());

        string filtered = await client.GetStringAsync("/gallery?type=systematic&sort=title");
        Assert.Contains($"/gallery/{entry.Id}", filtered);
    }

    [Fact]
    public async Task AnEntryShowsOnlyThePrintedReportAndHowItWasMade()
    {
        var entry = await AddedEntryAsync("AI <script>alert(1)</script> in schools");
        using var host = new Host(GalleryFolder);
        using var client = host.CreateClient();

        var page = await client.GetAsync($"/gallery/{entry.Id}");
        page.EnsureSuccessStatusCode();
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("How this was made", html);
        Assert.Contains("ui-print-view", html);
        Assert.Contains("AI &lt;script&gt;alert(1)&lt;/script&gt; in schools", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        // The run's own actions and its links into the run folder are not part of the gallery's view.
        Assert.DoesNotContain("Download archive", html);
        Assert.DoesNotContain("Add to the gallery?", html);
        Assert.DoesNotContain("/api/workspace/", html);
        Assert.Contains($"run {entry.RunId}", html);
    }

    [Fact]
    public async Task AHiddenOrUnknownEntryIsNotFound()
    {
        var entry = await AddedEntryAsync("A hidden review");
        using var host = new Host(GalleryFolder, hidden: entry.Id);
        using var client = host.CreateClient();

        Assert.DoesNotContain(entry.Id, await client.GetStringAsync("/gallery"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/gallery/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/gallery/000000000000")).StatusCode);
    }

    [Fact]
    public void ThePrintViewCoversEveryPrintUtilityThePagesUse()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Join(dir, "AuBtechReviewAgent.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return; // not run from the repository (packaged tests)
        string app = Path.Join(dir, "AuBtechReviewAgent");
        string css = File.ReadAllText(Path.Join(app, "Styles", "tailwind.input.css"));
        var used = new[] { "Components", "Modules" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Join(app, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?<![\w-])print:([\w\[\]\.\-]+)").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.NotEmpty(used);
        var missing = used.Where(u => !css.Contains(".ui-print-view .print\\:" + u.Replace("[", "\\[").Replace("]", "\\]").Replace(".", "\\.") + " ")).ToList();
        Assert.True(missing.Count == 0, "Add these to the print view in Styles/tailwind.input.css: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheConsentSaysWhatIsPublishedAndThatItCanBeWithdrawn()
    {
        string text = string.Join(" ", GalleryText.Consent(7));
        Assert.Contains("as it prints", text);
        Assert.Contains("quotes", text);
        Assert.Contains("public", text);
        Assert.Contains("research questions will be visible", text);
        Assert.Contains("withdraw it at any time", text);
        Assert.Contains("7 days", text);
    }
}

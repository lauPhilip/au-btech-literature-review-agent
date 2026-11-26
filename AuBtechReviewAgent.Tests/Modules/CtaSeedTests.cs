using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Seeds the app's run folder with a finished fake run of each kind of review, for the CI check that every link and
/// button on the report pages works (tools/a11y/cta-check.mjs). It does nothing unless CTA_SEED_DIR names the folder to
/// fill; CI sets it, with CTA_SEED_KEYS naming the file that receives each run's id and edit key, so the check can open
/// each page as its owner and as a reader. The runs are the module kit's, poisoned on purpose: one source carries a
/// javascript: link, which must never become a link on a page.
/// </summary>
public class CtaSeedTests
{
    [Fact]
    public async Task SeedTheReportPagesWhenAsked()
    {
        string? target = Environment.GetEnvironmentVariable("CTA_SEED_DIR");
        string? keysFile = Environment.GetEnvironmentVariable("CTA_SEED_KEYS");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(keysFile)) return;

        var store = new RunStore(target);
        var seeded = new Dictionary<string, Dictionary<string, string>>();
        foreach (var kind in new[] { "systematic", "multivocal" })
        {
            var run = await ModuleKit.RunAsync(kind);
            var runId = Guid.ParseExact(Path.GetFileName(run.Folder), "N");
            string folder = store.FolderOf(runId);
            Copy(run.Folder, folder);
            File.Delete(Path.Join(folder, RunStore.OwnerFile));
            seeded[kind] = new() { ["id"] = runId.ToString(), ["key"] = store.ClaimRun(runId) };
        }
        File.WriteAllText(keysFile, JsonSerializer.Serialize(seeded));
        Assert.All(seeded.Values, run => Assert.True(store.HasRun(Guid.Parse(run["id"]))));
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Join(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Join(to, Path.GetFileName(dir)));
    }
}

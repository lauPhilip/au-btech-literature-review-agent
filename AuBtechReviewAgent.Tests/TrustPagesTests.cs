using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The trust features: structured data, security.txt, the statement on AI use and the archive check.</summary>
public class TrustPagesTests
{
    private const string Base = "https://traceable.example.org";

    [Fact]
    public void StructuredDataIsValidJsonOfTheRightType()
    {
        foreach (var (json, type) in new[]
        {
            (SiteSeo.DatasetJsonLd(Base, 12), "Dataset"),
            (SiteSeo.ScholarlyArticleJsonLd(Base), "ScholarlyArticle"),
            (SiteSeo.BreadcrumbJsonLd(Base, new[] { ("TraceableAI", "/"), ("About", "/about") }), "BreadcrumbList"),
        })
        {
            Assert.DoesNotContain("<", json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(type, doc.RootElement.GetProperty("@type").GetString());
        }
        using var crumbs = JsonDocument.Parse(SiteSeo.BreadcrumbJsonLd(Base, new[] { ("TraceableAI", "/"), ("About", "/about") }));
        Assert.Equal(Base + "/about", crumbs.RootElement.GetProperty("itemListElement")[1].GetProperty("item").GetString());
    }

    [Fact]
    public void SecurityTxtHasAContactAndExpiresWithinAYear()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        string txt = SiteSeo.SecurityTxt(Base, now);

        Assert.Contains("Contact: https://github.com/", txt);
        var expires = DateTime.Parse(txt.Split('\n').Single(l => l.StartsWith("Expires: ")).Substring(9), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expires > now && expires < now.AddYears(1));
        Assert.Contains($"Canonical: {Base}/.well-known/security.txt", txt);
    }

    private static ReviewState FinishedRun(bool humanCheck) => new()
    {
        Timestamp = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
        CompletedUtc = new DateTime(2026, 10, 1, 9, 12, 0, DateTimeKind.Utc),
        DualScreeningRequested = true,
        HumanScreeningReviewRequested = humanCheck,
        RunSettings = new RunSettingsRecord { Model = "mistral-large-latest", AppVersion = "1.6.0+abc123" },
        Stats = new ReviewStats
        {
            DualScreened = 64, ScreeningKappa = 0.81, HumanReviewed = humanCheck ? 12 : 0, HumanOverrides = humanCheck ? 2 : 0,
            CitationsChecked = 96, CitationsSupported = 81, CitationsPartiallySupported = 11, CitationsNotSupported = 4,
        },
    };

    [Fact]
    public void TheAiStatementSaysWhatTheRunDidAndNothingElse()
    {
        string text = AiUseStatement.Methods(FinishedRun(humanCheck: false), Guid.Empty);

        Assert.Contains("mistral-large-latest", text);
        Assert.Contains("version 1.6.0)", text);
        Assert.Contains("1 October 2026", text);
        Assert.Contains("Cohen's kappa = 0.81", text);
        Assert.Contains("Of 96 citations", text);
        Assert.Contains("81 were judged supported", text);
        Assert.Contains("not checked by a human reviewer", text);
        Assert.DoesNotContain("citing papers", text); // citation chaining was not used
        Assert.Contains("take full responsibility", AiUseStatement.Short(FinishedRun(false)));
    }

    [Fact]
    public void TheAiStatementReportsTheHumanCheckWhenThereWasOne()
    {
        string text = AiUseStatement.Methods(FinishedRun(humanCheck: true), Guid.Empty);
        Assert.Contains("A human reviewer checked 12 screening decisions and changed 2 of them.", text);
    }

    private static byte[] Archive(Action<Dictionary<string, byte[]>>? tamper = null, bool editManifest = false)
    {
        var files = new List<(string Path, byte[] Content)>
        {
            ("protocol.md", Encoding.UTF8.GetBytes("# Review protocol")),
            ("ledger/screening.json", Encoding.UTF8.GetBytes("[]")),
        };
        string manifest = RunManifest.Build(Guid.Parse("3f2b6c1e-8a44-4d0b-9c1a-5e7f0a1b2c3d"), null, files);
        if (editManifest) manifest = manifest.Replace("\"HashAlgorithm\": \"SHA-256\"", "\"HashAlgorithm\": \"SHA-1\"");
        var all = files.ToDictionary(f => f.Path, f => f.Content);
        all["manifest.json"] = Encoding.UTF8.GetBytes(manifest);
        tamper?.Invoke(all);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, content) in all)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(content);
            }
        return ms.ToArray();
    }

    [Fact]
    public void AnUntouchedArchivePasses()
    {
        var check = RunManifest.CheckArchive(new MemoryStream(Archive()));

        Assert.True(check.Unchanged);
        Assert.True(check.ManifestIntact == true);
        Assert.Equal(2, check.FilesListed);
        Assert.Equal("3f2b6c1e-8a44-4d0b-9c1a-5e7f0a1b2c3d", check.RunId);
    }

    [Fact]
    public void ChangedMissingAndAddedFilesAreEachReported()
    {
        var check = RunManifest.CheckArchive(new MemoryStream(Archive(f =>
        {
            f["protocol.md"] = Encoding.UTF8.GetBytes("# Review protocol, edited");
            f.Remove("ledger/screening.json");
            f["extra.txt"] = Encoding.UTF8.GetBytes("added later");
        })));

        Assert.False(check.Unchanged);
        Assert.Equal(new[] { "protocol.md" }, check.Changed);
        Assert.Equal(new[] { "ledger/screening.json" }, check.Missing);
        Assert.Equal(new[] { "extra.txt" }, check.NotListed);
    }

    [Fact]
    public void AnEditedManifestIsCaughtByItsOwnFingerprint()
    {
        var check = RunManifest.CheckArchive(new MemoryStream(Archive(editManifest: true)));

        Assert.True(check.ManifestIntact == false);
        Assert.False(check.Unchanged);
    }

    [Fact]
    public void AnArchiveWithoutManifestOrTooLargeIsRejected()
    {
        var noManifest = RunManifest.CheckArchive(new MemoryStream(Archive(f => f.Remove("manifest.json"))));
        Assert.False(noManifest.HasManifest);
        Assert.False(noManifest.Unchanged);

        Assert.ThrowsAny<InvalidDataException>(() => RunManifest.CheckArchive(new MemoryStream(Archive()), maxUnpackedBytes: 10));
        Assert.ThrowsAny<InvalidDataException>(() => RunManifest.CheckArchive(new MemoryStream(Encoding.UTF8.GetBytes("not a zip"))));
    }
}

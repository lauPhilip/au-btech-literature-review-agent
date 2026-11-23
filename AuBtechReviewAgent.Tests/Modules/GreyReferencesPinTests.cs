using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A grey literature review's reference files, pinned byte for byte, so that adding formal sources (block I-6) cannot
/// change how a grey source is cited: a page, a repository cited by its creation year and last update (#345), and a
/// source without a producer or date.
/// </summary>
public class GreyReferencesPinTests
{
    public static (MultivocalLedger, MultivocalPagesFile, MultivocalExtractionFile, MultivocalScreeningFile) Run()
    {
        var ledger = new MultivocalLedger
        {
            Sources =
            {
                new FoundGreySource { Record = new GreyRecord("x:1", "Managing agent context", "", "https://blog.example.org/post", "Ana Berg", "blog.example.org", "blogs", new DateTime(2025, 5, 1), new Dictionary<string, long>()) },
                new FoundGreySource { Record = new GreyRecord("x:2", "agent-memory: a toolkit", "", "https://github.com/acme/agent-memory", "acme", "github.com", "code", new DateTime(2023, 2, 3), new Dictionary<string, long> { ["stars"] = 12 }, new DateTime(2026, 9, 30)) },
                new FoundGreySource { Record = new GreyRecord("x:3", "Effective context & memory_management", "", "https://docs.acme.dev/guide", "", "docs.acme.dev", "documentation", null, new Dictionary<string, long>()) },
            },
        };
        var pages = new MultivocalPagesFile
        {
            Pages =
            {
                new PageSnapshot { Url = "https://blog.example.org/post", AccessedUtc = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc), WaybackUrl = "https://web.archive.org/web/20261007090000/https://blog.example.org/post" },
                new PageSnapshot { Url = "https://github.com/acme/agent-memory", AccessedUtc = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc), WaybackUrl = "https://web.archive.org/web/20261008090000/https://github.com/acme/agent-memory" },
                new PageSnapshot { Url = "https://docs.acme.dev/guide", AccessedUtc = new DateTime(2026, 10, 9), NotKept = "robots.txt does not allow it" },
            },
        };
        var extraction = new MultivocalExtractionFile
        {
            Sources =
            {
                new GreyExtraction { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post" },
                new GreyExtraction { Address = "github.com/acme/agent-memory", Title = "agent-memory: a toolkit", Url = "https://github.com/acme/agent-memory" },
                new GreyExtraction { Address = "docs.acme.dev/guide", Title = "Effective context & memory_management", Url = "https://docs.acme.dev/guide" },
            },
        };
        var screening = new MultivocalScreeningFile
        {
            Sources =
            {
                new GreyScreening { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post", Decision = "Included", Uncertain = true, Reviewed = true, Reasoning = "On agents." },
                new GreyScreening { Address = "github.com/acme/agent-memory", Title = "agent-memory: a toolkit", Url = "https://github.com/acme/agent-memory", Decision = "Included" },
                new GreyScreening { Address = "docs.acme.dev/guide", Title = "Effective context & memory_management", Url = "https://docs.acme.dev/guide", Decision = "Excluded", ExclusionReason = ExclusionReasons.OffTopic },
            },
        };
        return (ledger, pages, extraction, screening);
    }

    private static string Sha(string text) =>
        RunManifest.Sha256(new System.Text.UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n")));

    [Fact]
    public void AGreyLiteratureReviewsReferenceFilesAreUnchanged()
    {
        var (ledger, pages, extraction, screening) = Run();
        var references = GreyReferences.Build(ledger, pages, extraction);

        // The fingerprints of the files as written before block I-6; a change here changes every grey review's references.
        Assert.Equal("67cda2635bb7164948f7fce9e239dee00c06e6cad25006022ea3d125816fc761", Sha(GreyReferences.ToBibTeX(references)));
        Assert.Equal("d1a703facc24ed052ae615a3b0446e9956de2e1a589e886bb55ca530ba71e5d1", Sha(GreyReferences.ToRis(references)));
        Assert.Equal("5061e3b6a5296bc5a124467c2e7bbcffbaa38770073cbc236344ceca0b8521c9",
            Sha(GreyReferences.ToScreenedRis(ledger, screening, MultivocalReporter.SourceNumbers(extraction))));
        Assert.Equal("b25b79219b479476335dc5ad62fc941821071cdd34bead282a6ece5438c36a7c", Sha(string.Join("\n", references.Select(GreyReferences.Line))));
        Assert.Equal("acme (2023). agent-memory: a toolkit. *github.com*. https://github.com/acme/agent-memory (last updated 2026-09-30; accessed 2026-10-08; archived at https://web.archive.org/web/20261008090000/https://github.com/acme/agent-memory)",
            GreyReferences.Line(references[1]));
    }
}

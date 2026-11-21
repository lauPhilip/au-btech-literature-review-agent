using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report page after redesign step 8 (Components/Pages/SpecMatrix.razor, read as source) and the sample run
/// behind /design/report (Components/Pages/Design/SampleReport.cs). Issues #198 to #200.
/// </summary>
public class ReportPageTests
{
    private static string? Source(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent", "Components" }.Concat(path).ToArray()));
    }

    [Fact]
    public void EverySectionInTheContentsListHasItsIdOnThePage()
    {
        string? page = Source("Pages", "SpecMatrix.razor");
        if (page == null) return; // not run from the repository (packaged tests)

        foreach (string id in new[] { "AbstractId", "IntroductionId", "MethodologyId", "StudiesId", "ResultsId", "DiscussionId", "ConclusionId", "DeclarationsId", "ReferencesId", "AppendixId" })
            Assert.Contains($"id=\"@AuBtechReviewAgent.ReportOutline.{id}\" data-report-section", page);
        Assert.Contains("id=\"@AuBtechReviewAgent.ReportOutline.ThemeId(k)\" data-report-section", page);
        Assert.Contains("<ReportContents Entries=\"Outline\"", page);
        Assert.Contains("<CitationCheckPanel ", page);
    }

    [Fact]
    public void TheReportKeepsWhatTheOldPageCouldDo()
    {
        string? page = Source("Pages", "SpecMatrix.razor");
        if (page == null) return;

        // Printing, the archive, the exports, the link, a new review from the settings, deleting, notes and re-checks.
        foreach (string kept in new[] { "PrintReport", "/archive", "/references.bib", "/references.ris", "/screened.ris", "/protocol.md",
                     "CopyLink", "/review?from=", "DeleteRun", "SaveNoteAsync", "RecheckAsync", "CopyStatement" })
            Assert.Contains(kept, page);
        // The old full-page style block is gone: the frame scrolls the content area and drops itself on paper.
        Assert.DoesNotContain("overflow-y: auto !important", page);
        Assert.DoesNotContain("max-w-[8.5in]", page);
    }

    [Fact]
    public void EveryCitationInTheSampleHasAVerdictOnItsOwnSentence()
    {
        var report = SampleReport.Report();
        var checks = SampleReport.Checks(report);

        Assert.NotEmpty(checks);
        foreach (var c in checks)
            Assert.Contains(c.Reference, CitationSupportChecker.ReferencesIn(c.Sentence));
        // Every verdict appears, so every colour shows on the page.
        foreach (string verdict in new[] { CitationSupportChecker.Supported, CitationSupportChecker.Partial, CitationSupportChecker.NotSupported,
                     CitationSupportChecker.InsufficientEvidence, CitationSupportChecker.Unverifiable })
            Assert.Contains(checks, c => c.Verdict == verdict);
        Assert.Equal(report.SynthesisSections.Count, ReportOutline.Systematic(report).Count(e => e.Level == 2));
    }

    [Fact]
    public void TheSampleRunIsWrittenAsTheReportPageReadsIt()
    {
        string folder = Path.Join(Path.GetTempPath(), "traceableai-sample-" + Guid.NewGuid().ToString("N"));
        try
        {
            SampleReport.Write(folder);

            var state = JsonSerializer.Deserialize<ReviewState>(File.ReadAllText(Path.Join(folder, "transparent-process.json")));
            var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            using var audit = JsonDocument.Parse(File.ReadAllText(Path.Join(folder, "citation-audit.json")));
            var checks = JsonSerializer.Deserialize<List<CitationSupportResult>>(audit.RootElement.GetProperty("SupportChecks").GetRawText());

            Assert.NotNull(state);
            Assert.NotNull(report);
            Assert.NotNull(checks);
            Assert.Equal(6, state!.SynthesizedRecords.Count);
            Assert.Equal(checks!.Count, state.Stats.CitationsChecked);
            Assert.True(PrismaFlowCounts.From(state).IsConsistent, "the sample's flow of records adds up");
            Assert.True(File.Exists(Path.Join(folder, "protocol.md")));
            Assert.Equal(SampleReport.Report().TitleItem, report!.TitleItem);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

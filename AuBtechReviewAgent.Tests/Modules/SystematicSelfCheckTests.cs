using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The self-check of a finished systematic review: the fake run of the module kit (scripted fake model) checks its
/// own paper, writes self-check.json, puts it in the archive and counts the outcomes in the ledger.
/// </summary>
public class SystematicSelfCheckTests
{
    [Fact]
    public async Task AFinishedRunChecksItsPaperAndKeepsTheResultInTheArchiveAndLedger()
    {
        var run = await ModuleKit.RunAsync("systematic");

        var file = JsonSerializer.Deserialize<PaperSelfCheckFile>(run.Archive[PaperSelfCheckFile.FileName])!;
        Assert.Equal(10, file.Checks.Count);
        Assert.Equal(10, file.Passed + file.Notes + file.Faults);
        Assert.All(file.Checks, c => Assert.Contains(c.Outcome, new[] { CheckOutcome.Pass, CheckOutcome.Note, CheckOutcome.Fault }));

        var ledger = GallerySnapshot.LoadLedger(run.Folder)!;
        Assert.Equal(file.Passed, ledger.Stats.SelfCheckPassed);
        Assert.Equal(file.Notes, ledger.Stats.SelfCheckNotes);
        Assert.Equal(file.Faults, ledger.Stats.SelfCheckFaults);
        Assert.Equal(file.Faults, SystematicSelfCheck.Load(run.Folder)!.Faults);
    }

    [Fact]
    public void TheFactsNameEachCountOfTheLedger()
    {
        var state = new ReviewState
        {
            Stats = new ReviewStats { TotalIdentified = 40, DuplicatesRemoved = 6, Screened = 34, Included = 4, Excluded = 30, FullTextRetrieved = 3 },
            SynthesizedRecords = new() { new() { VenueType = "Journals" }, new() { VenueType = "Journals" }, new() { VenueType = "Preprints" }, new() { VenueType = "Other" } },
        };

        var facts = SystematicSelfCheck.Facts(state).ToDictionary(f => f.Label, f => f.Value);

        Assert.Equal(40, facts["records identified"]);
        Assert.Equal(1, facts["studies read from the abstract"]);
        Assert.Equal(2, facts["journal papers"]);
        Assert.Equal(1, facts["preprint papers"]);
        Assert.False(facts.ContainsKey("themes"));
    }

    [Fact]
    public void ARunWithoutAReportHasNoSelfCheck()
    {
        string folder = Path.Join(Path.GetTempPath(), "selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(SystematicSelfCheck.WriteFile(folder, new ReviewState()));
            Assert.False(File.Exists(Path.Join(folder, PaperSelfCheckFile.FileName)));
        }
        finally { TestFolders.TryDelete(folder); }
    }

    [Fact]
    public void AStudyPlacedInNoThemeIsNotCitedWithTheCodebooksReason()
    {
        var state = new ReviewState
        {
            Stats = new ReviewStats { Included = 2 },
            SynthesizedRecords = new() { new() { ReferenceNumber = 1 }, new() { ReferenceNumber = 2 } },
            ThematicSynthesis = new ThematicCodebook
            {
                Themes = new() { new Theme { Id = "T1", Name = "Defects", Studies = new() { 1 }, SingleStudyReason = "the only study of defects" } },
                Uncoded = new() { new UncodedStudy(2, "about design, not safety") },
            },
        };
        var report = new PrismaReport
        {
            AbstractItem = "Two studies were included.",
            SynthesisSections = new() { new SynthesisSection { Field = "synthesis-1", ThemeId = "T1", Heading = "Defects", Text = "Agents cut defects [1]." } },
        };

        var check = PaperSelfCheck.Run(SystematicSelfCheck.Input(report, state, new SystematicSelfCheck.Audit(null, null, 0, Array.Empty<UncodedStudy>())))
            .Checks.Single(c => c.Id == "coverage");

        Assert.Equal(CheckOutcome.Note, check.Outcome);
        Assert.Equal("Not cited: placed in no theme: about design, not safety", check.Evidence[0].Detail);
        Assert.Equal("Rests on one study: the only study of defects", check.Evidence[1].Detail);
    }
}

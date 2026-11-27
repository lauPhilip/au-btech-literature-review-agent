using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issues #680 and #695: the self-check reads the concept map as a place after the results, and notes it when more than
/// half of the map's links, counted before the cap, are the review's own reading.
/// </summary>
public class PaperSelfCheckMapTests
{
    private static ReviewArtifact Map(string nodeLabel) => new()
    {
        Kind = ArtifactKinds.ConceptMap,
        Nodes = new() { new() { Id = "n1", Label = nodeLabel }, new() { Id = "n2", Label = "Audit logs [3]" } },
        Edges = new() { new() { From = "n1", To = "n2", Label = "supports" } },
    };

    [Fact]
    public void AFigureRepeatedInTheConceptMapAndOnePlaceAfterTheResultsIsAFaultInCheck5()
    {
        var input = SelfCheckPaper.Input(theme1: "Latency fell by 93.12% [1].", answer1: "Latency fell by 93.12% [1].");
        input.Parts.Add(new PaperPart("artifact", "Concept map", PaperPartKind.ConceptMap, SystematicSelfCheck.MapText(Map("Latency fell by 93.12% [1]"))));

        var check = SelfCheckPaper.Check(input, "figures");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        var e = Assert.Single(check.Evidence);
        Assert.Equal("Concept map", e.Part);
        Assert.Contains("93.12", e.Detail);
    }

    [Fact]
    public void AFigureInTheConceptMapAloneIsAPlaceAfterTheResultsAndStillPasses()
    {
        var input = SelfCheckPaper.Input(theme1: "Latency fell by 93.12% [1].", answer1: "Agents help [1].");
        input.Parts.Add(new PaperPart("artifact", "Concept map", PaperPartKind.ConceptMap, SystematicSelfCheck.MapText(Map("Latency fell by 93.12% [1]"))));

        Assert.Equal(CheckOutcome.Pass, SelfCheckPaper.Check(input, "figures").Outcome);
    }

    [Fact]
    public void TheMapTextHoldsEveryBoxAndLinkLabelAndNothingForAnEmptyTableOrAnError()
    {
        Assert.Equal("Latency fell by 93.12% [1].\nAudit logs [3].\nsupports.", SystematicSelfCheck.MapText(Map("Latency fell by 93.12% [1]")));
        Assert.Equal("", SystematicSelfCheck.MapText(new ReviewArtifact { Kind = ArtifactKinds.Table }));
        Assert.Equal("", SystematicSelfCheck.MapText(new ReviewArtifact { Kind = ArtifactKinds.Bullets, Bullets = { "Gains [1]" }, Error = "no items" }));
        Assert.Equal("", SystematicSelfCheck.MapText(null));
    }

    // Issue #702: a comparison table or a list repeats figures as well as a diagram does.
    [Fact]
    public void TheTextOfATableIsOneLinePerRowAndOfAListOneLinePerItem()
    {
        var table = new ReviewArtifact { Kind = ArtifactKinds.Table, Columns = { "Study", "Result" }, Rows = { new() { "[1]", "Latency fell by 93.12%." }, new() { "[2]", " ", "Fewer errors" } } };
        var list = new ReviewArtifact { Kind = ArtifactKinds.Bullets, Bullets = { "Latency fell by 93.12% [1].", "", "Audit logs help [3]" } };

        Assert.Equal("[1]; Latency fell by 93.12%.\n[2]; Fewer errors.", SystematicSelfCheck.MapText(table));
        Assert.Equal("Latency fell by 93.12% [1].\nAudit logs help [3].", SystematicSelfCheck.MapText(list));
    }

    [Fact]
    public void AFigureRepeatedInAComparisonTableIsAFaultInCheck5()
    {
        var table = new ReviewArtifact { Kind = ArtifactKinds.Table, Columns = { "Study", "Result" }, Rows = { new() { "[1]", "Latency fell by 93.12%" } } };
        var input = SelfCheckPaper.Input(theme1: "Latency fell by 93.12% [1].", answer1: "Latency fell by 93.12% [1].");
        input.Parts.Add(new PaperPart("artifact", "Comparison table", PaperPartKind.ConceptMap, SystematicSelfCheck.MapText(table)));

        var check = SelfCheckPaper.Check(input, "figures");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Equal("Comparison table", Assert.Single(check.Evidence).Part);
    }

    [Fact]
    public void TheMapTextDoesNotChangeTheOtherChecksOfTheSameParts()
    {
        var without = SelfCheckPaper.Input(theme1: "Agents help [1]. A second study agrees [2].");
        var with = SelfCheckPaper.Input(theme1: "Agents help [1]. A second study agrees [2].");
        with.Parts.Add(new PaperPart("artifact", "Concept map", PaperPartKind.ConceptMap, "Unrelated words [4]. This is a box that goes on, and on, and on, and on without end."));

        var a = PaperSelfCheck.Run(without);
        var b = PaperSelfCheck.Run(with);

        Assert.Equal(a.Checks.Select(c => (c.Id, c.Outcome, c.Summary)), b.Checks.Select(c => (c.Id, c.Outcome, c.Summary)));
    }

    [Fact]
    public void MoreThanHalfOfTheLinksBeingTheReviewsOwnReadingIsANoteOnTheCitationCheck()
    {
        var input = new PaperCheckInput { MapLinks = new PaperMapLinks(15, 12, 9), Included = new() { 1 } };

        var check = SelfCheckPaper.Check(input, "citation-check");

        Assert.Equal(CheckOutcome.Note, check.Outcome);
        var e = Assert.Single(check.Evidence);
        Assert.Equal("12 of 15 links are the review's own reading", e.Text);
        Assert.Contains("9 dashed links were left out", e.Detail);
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(15, 3)]
    [InlineData(0, 0)]
    public void HalfOrFewerDashedLinksAddNoNote(int total, int reading)
    {
        var check = SelfCheckPaper.Check(new PaperCheckInput { MapLinks = new PaperMapLinks(total, reading, 0) }, "citation-check");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Empty(check.Evidence);
    }

    [Fact]
    public void TheLinkShareIsReadFromTheLinksFileBeforeTheCapAndMissingOrUnreadableFilesGiveNone()
    {
        string folder = Path.Join(Path.GetTempPath(), "selfcheck-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(SystematicSelfCheck.ReadMapLinks(folder));
            var decisions = new List<ArtifactLinkCheck>
            {
                new() { Check = ArtifactLinks.Supported },
                new() { Check = ArtifactLinks.Reading }, new() { Check = ArtifactLinks.Reading },
                new() { Check = ArtifactLinks.Reading, Dropped = true },
            };
            File.WriteAllText(Path.Join(folder, ArtifactLinks.FileName), JsonSerializer.Serialize(decisions));

            var links = SystematicSelfCheck.ReadMapLinks(folder)!;

            Assert.Equal(new PaperMapLinks(4, 3, 1), links);
            Assert.True(links.MostlyReading);
            Assert.Equal(links, SystematicSelfCheck.ReadAudit(folder).MapLinks);
            File.WriteAllText(Path.Join(folder, ArtifactLinks.FileName), "not json");
            Assert.Null(SystematicSelfCheck.ReadMapLinks(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheSystematicInputGetsTheMapAsAPartAfterTheResultsAndTheLinksFromTheAudit()
    {
        var report = new PrismaReport { AbstractItem = "Abstract.", Artifact = Map("Latency fell by 93.12% [1]") };
        var audit = new SystematicSelfCheck.Audit(null, null, 0, Array.Empty<UncodedStudy>(), MapLinks: new PaperMapLinks(5, 4, 1));

        var input = SystematicSelfCheck.Input(report, new ReviewState(), audit);

        var part = Assert.Single(input.Parts, p => p.Kind == PaperPartKind.ConceptMap);
        Assert.Contains("Latency fell by 93.12% [1].", part.Text);
        Assert.Equal(new PaperMapLinks(5, 4, 1), input.MapLinks);
        Assert.DoesNotContain(input.Parts, p => p.Kind == PaperPartKind.ConceptMap && p.Text.Length == 0);
    }
}

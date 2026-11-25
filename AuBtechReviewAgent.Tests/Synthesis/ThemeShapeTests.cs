using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #464: in the multivocal run 50e9a8e3 "cite every source of the theme" turned themes of 60 to 130 sources into
/// lists of one-source summaries, and the same claims came back theme after theme. A small theme still cites every
/// source with its fill pass; a large one is synthesised, sent back when it reads as a list, and a claim another theme
/// already reports is pointed out and revised.
/// </summary>
public class ThemeShapeTests
{
    private const string List = "A debate loop uses three roles [1]. A planner splits the task [2]. A library runs a search loop [3]. A tool splits its pipeline [4]. A guide prunes context [5]. A kit gates quality [6].";
    private const string Synthesised = "Several sources split work across roles [1, 2]. Two add a search loop [3, 4]. Quality gates are rarer [6]. One guide prunes context [5], while another gates quality [6].";

    [Fact]
    public void ASmallThemeCitesEverySourceAsBeforeAndALargeOneIsSynthesised()
    {
        Assert.Equal("Cite every one of these studies at least once: [1], [2], [3], [4], [5]. A study may be cited together with others when they report the same finding (\"... [3, 7, 12]\").",
            ReviewWriter.WhichSourcesRule(WritingProfile.Systematic, new[] { 1, 2, 3, 4, 5 }));
        string large = ReviewWriter.WhichSourcesRule(WritingProfile.Systematic, Enumerable.Range(1, 6).ToList());
        Assert.StartsWith("Synthesise, do not list:", large);
        Assert.Contains("This theme has 6 studies; you need not cite every one.", large);
    }

    [Fact]
    public void ALargeThemeWrittenAsAListOfOneSourceSummariesIsAList()
    {
        Assert.StartsWith("6 of its 6 cited sentences each summarise one study", ReviewWriter.ListingProblem(List, 6, WritingProfile.Systematic));
        Assert.Null(ReviewWriter.ListingProblem(Synthesised, 6, WritingProfile.Systematic));
        Assert.Null(ReviewWriter.ListingProblem(List, 5, WritingProfile.Systematic)); // a small theme is never judged a list
        // Two sentences on the same source are not a list.
        Assert.Null(ReviewWriter.ListingProblem("One logs [1]. It replays [1]. Two audit [2, 3]. One gates [4]. It scores [4]. Two prune [5, 6].", 6, WritingProfile.Systematic));
    }

    [Fact]
    public async Task ALargeThemeIsSentBackWhenItListsAndGetsNoFillPass()
    {
        var evidence = Evidence(7);
        var model = new FakeChatService()
            .Returns($$"""{"heading":"Logs","text":"{{List}}"}""")
            .Returns($$"""{"heading":"Logs","text":"{{Synthesised}}"}""");

        var (section, coverage) = await ReviewWriter.WriteThemeAsync(model, WritingProfile.Systematic, "Map logging", evidence, evidence.Themes[0], 1, referenceCount: 7);

        Assert.Equal(2, model.Prompts.Count); // no fill pass
        Assert.Contains("2. Synthesise, do not list:", model.Prompts[0]);
        Assert.DoesNotContain("Cite every one of these", model.Prompts[0]);
        Assert.Contains("reads as a list of studies", model.Prompts[1]);
        Assert.Equal(Synthesised, section!.Text);
        Assert.Equal(new[] { 7 }, coverage.NotCited);
        Assert.StartsWith("not run: a theme of 7 studies is synthesised", coverage.FillPass);
        Assert.Contains(coverage.WritingNotes!, n => n.StartsWith("The subsection was sent back for a list of one-source summaries (1 of 2)"));
    }

    [Fact]
    public void AClaimAnEarlierThemeReportsIsARepeat()
    {
        var sections = new List<SynthesisSection>
        {
            new() { Field = "synthesis-1", Heading = "Context", Text = "More context makes agents measurably worse at long tasks [48]. Summaries help [2]." },
            new() { Field = "synthesis-2", Heading = "Memory", Text = "Agents become measurably worse at long tasks with more context [48]. Stores persist notes [3]." },
        };
        var repeat = Assert.Single(ReviewWriter.RepeatedClaims(sections));
        Assert.Equal(("synthesis-2", "Agents become measurably worse at long tasks with more context [48].", "Context"), repeat);
    }

    [Fact]
    public async Task ARepeatedClaimIsRevisedIntoAPointerAndTheDecisionIsOnFile()
    {
        var evidence = new ReviewEvidence
        {
            Sources = Enumerable.Range(1, 3).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025)." }).ToList(),
            Themes = new[]
            {
                new EvidenceTheme { Id = "T1", Name = "Context", Sources = new[] { 1, 2 } },
                new EvidenceTheme { Id = "T2", Name = "Memory", Sources = new[] { 1, 3 } },
            },
        };
        var sections = new List<SynthesisSection>
        {
            new() { Field = "synthesis-1", ThemeId = "T1", Heading = "Context", Text = "More context makes agents measurably worse at long tasks [1]. Summaries help [2]." },
            new() { Field = "synthesis-2", ThemeId = "T2", Heading = "Memory", Text = "Agents become measurably worse at long tasks with more context [1]. Stores persist notes [3]." },
        };
        var coverage = new List<ThemeCoverage> { new() { Field = "synthesis-1" }, new() { Field = "synthesis-2" } };
        var model = new FakeChatService().Returns("""{"text":"As reported under Context, longer contexts hurt [1]. Stores persist notes [3]."}""");

        await ReviewWriter.RevisitRepeatsAsync(model, WritingProfile.Systematic, evidence, sections, coverage, 3);

        string prompt = Assert.Single(model.Prompts);
        Assert.Contains("- \"Agents become measurably worse at long tasks with more context [1].\" (already reported under \"Context\")", prompt);
        Assert.Equal("As reported under Context, longer contexts hurt [1]. Stores persist notes [3].", sections[1].Text);
        Assert.Equal(new[] { "1 sentence(s) that repeated an earlier theme were revised (0 left)." }, coverage[1].WritingNotes);
        Assert.Null(coverage[0].WritingNotes);
    }

    [Fact]
    public async Task ARevisionThatDropsACitationIsRejectedAndTheSubsectionKept()
    {
        var sections = new List<SynthesisSection>
        {
            new() { Field = "synthesis-1", Heading = "Context", Text = "More context makes agents measurably worse at long tasks [1]." },
            new() { Field = "synthesis-2", Heading = "Memory", Text = "Agents become measurably worse at long tasks with more context [1]. Stores persist notes [3]." },
        };
        var coverage = new List<ThemeCoverage> { new() { Field = "synthesis-2" } };
        var model = new FakeChatService().Returns("""{"text":"Stores persist notes [3]."}""");

        await ReviewWriter.RevisitRepeatsAsync(model, WritingProfile.Systematic, new ReviewEvidence(), sections, coverage, 3);

        Assert.StartsWith("Agents become", sections[1].Text);
        Assert.StartsWith("Revision of 1 repeated sentence(s) rejected: the revision dropped the citation of [1]", Assert.Single(coverage[0].WritingNotes!));
    }

    internal static ReviewEvidence Evidence(int sources, int themeSources = -1) => new()
    {
        Sources = Enumerable.Range(1, Math.Max(sources, 4)).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}.", Findings = new[] { new EvidenceFinding($"F{r}.1", $"Finding {r}", $"Quote {r}.") } }).ToList(),
        Themes = new[] { new EvidenceTheme { Id = "T1", Name = "Logs", Description = "Logging agent actions.", Sources = Enumerable.Range(1, themeSources < 0 ? sources : themeSources).ToList() } },
        BaseFacts = $"{sources} studies.",
    };
}

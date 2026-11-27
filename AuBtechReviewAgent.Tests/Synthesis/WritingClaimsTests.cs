using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issues #675 and #676 (run 8942b961): the abstract claimed more than answers of moderate certainty allow, reported
/// "removed 0 duplicates", and the rationale was one sentence per study. The front matter and the conclusion now get the
/// certainty of each answer with a rule on wording, and code sends back a settled claim, a zero count and a listed rationale.
/// </summary>
public class WritingClaimsTests
{
    private static readonly AnswerCertainty[] Moderate = { new("RQ1", "moderate"), new("RQ2", "low") };
    private static readonly AnswerCertainty[] OneHigh = { new("RQ1", "high"), new("RQ2", "low") };

    private const string Overclaim = "These findings show that effective safety requires combining validation layers with human judgment.";
    private const string Hedged = "These findings suggest that safety may benefit from combining validation layers with human judgment.";

    [Fact]
    public void ASettledClaimIsAProblemOnlyWhenNoAnswerIsOfHighCertainty()
    {
        Assert.StartsWith($"the abstract: \"{Overclaim}\" states a claim as settled (\"show that\"), but no answer is of high certainty (RQ1 moderate, RQ2 low)",
            ReviewWriter.OverclaimProblem(Overclaim, Moderate, "the abstract"));
        Assert.Null(ReviewWriter.OverclaimProblem(Hedged, Moderate, "the abstract"));
        Assert.Null(ReviewWriter.OverclaimProblem(Overclaim, OneHigh, "the abstract"));
        Assert.Null(ReviewWriter.OverclaimProblem(Overclaim, null, "the abstract"));
        Assert.Null(ReviewWriter.OverclaimProblem(Overclaim, new[] { new AnswerCertainty("RQ1", null) }, "the abstract"));
        Assert.NotNull(ReviewWriter.OverclaimProblem("Two studies demonstrate gains.", Moderate, "the conclusion"));
    }

    [Fact]
    public void TheWordingRuleFollowsTheLevelsAndIsEmptyWithoutThem()
    {
        Assert.Equal("", ReviewWriter.HedgeRule(null, "the abstract"));
        Assert.Equal("", ReviewWriter.CertaintyBlock(null));
        Assert.StartsWith(" No answer is of high certainty, so the abstract states every general claim with hedged words such as \"suggest\", \"may\"", ReviewWriter.HedgeRule(Moderate, "the abstract"));
        Assert.StartsWith(" In the conclusion, a claim that rests on an answer of high certainty may be stated firmly", ReviewWriter.HedgeRule(OneHigh, "the conclusion"));
        Assert.EndsWith("\nRQ1: moderate\nRQ2: low", ReviewWriter.CertaintyBlock(Moderate));
    }

    [Theory]
    [InlineData("We removed 0 duplicates.", true)]
    [InlineData("The search found 0 eligible records.", true)]
    [InlineData("A human checked the decisions and changed 0.", true)]
    [InlineData("No duplicates were found.", false)]
    [InlineData("We removed 10 duplicates.", false)]
    [InlineData("Version 2.0 records were read.", false)]
    public void AZeroCountInDigitsIsFound(string text, bool problem) =>
        Assert.Equal(problem, ReviewWriter.ZeroCountProblem(text, "the abstract") != null);

    private const string ListedRationale = "Focus Agent runs virtual focus groups [9]. MASON adds latency in complex workflows [7]. Plan reuse cuts cost [5]. A survey maps agent faults [3]. Little is known about how these mechanisms combine.";
    private const string ArguedRationale = "Agent systems now act with little oversight [3, 7]. Their safeguards are studied one at a time, in single workflows [5, 7]. How these safeguards combine across workflows is not yet known.";

    [Fact]
    public void ARationaleOfOneSentencePerStudyIsAList()
    {
        Assert.StartsWith("4 of the rationale's 4 cited sentences each summarise one study", ReviewWriter.RationaleListingProblem(ListedRationale, WritingProfile.Systematic));
        Assert.Null(ReviewWriter.RationaleListingProblem(ArguedRationale, WritingProfile.Systematic));
        Assert.Contains("what is known, what is missing, and why that matters", ReviewWriter.RationaleRule(WritingProfile.Systematic));
    }

    private static string Front(string abstractText, string rationale) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        { ["title"] = "Agent safety: a systematic review", ["abstract"] = abstractText, ["rationale"] = rationale, ["objectives"] = "To map agent safety." });

    private static readonly SynthesisSection[] Sections = { new() { Field = "synthesis-1", Heading = "Safety", Text = "Agent systems act with little oversight [3, 7]. Safeguards are studied in single workflows [5, 7]. Plan reuse cuts cost [5]. A survey maps agent faults [3]. Focus groups run online [9]." } };

    [Fact]
    public async Task TheFrontMatterIsSentBackForAnOverclaimAZeroCountAndAListedRationaleAndThenKept()
    {
        var notes = new List<string>();
        var model = new FakeChatService()
            .Returns(Front(Overclaim, ArguedRationale))
            .Returns(Front("The review removed 0 duplicates. " + Hedged, ArguedRationale))
            .Returns(Front(Hedged, ListedRationale));

        var front = await ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent safety", "Map agent safety", "Studies: 9. Duplicates removed: 0.",
            Sections, "", "[3] C. [5] E. [7] G. [9] I.", 9, notes.Add, certainty: Moderate);

        Assert.Contains("CERTAINTY OF THE EVIDENCE BEHIND EACH ANSWER", model.Prompts[0]);
        Assert.Contains("No answer is of high certainty, so the abstract states every general claim", model.Prompts[0]);
        Assert.Contains("never write one sentence per study", model.Prompts[0]);
        Assert.Contains("states a claim as settled (\"show that\")", model.Prompts[1]);
        Assert.Contains("reports a count of zero", model.Prompts[2]);
        // Two send-backs are the budget of the soft checks; the third answer is kept, with the listed rationale on file.
        Assert.Equal(3, model.Prompts.Count);
        Assert.Equal(Hedged, front!.Abstract);
        Assert.Contains(notes, n => n.StartsWith("The front-matter call was kept after 2 send-backs with a rationale that lists the sources"));
    }

    [Fact]
    public async Task WithoutRatingsTheFrontMatterPromptHasNoCertaintyBlock()
    {
        var model = new FakeChatService().Returns(Front(Overclaim, ArguedRationale));
        var front = await ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent safety", "Map agent safety", "Studies: 9.",
            Sections, "", "[3] C. [5] E. [7] G. [9] I.", 9);

        Assert.Equal(Overclaim, front!.Abstract);
        Assert.DoesNotContain("CERTAINTY OF THE EVIDENCE", Assert.Single(model.Prompts));
    }
}

/// <summary>
/// Issues #674 and #675 (run 8942b961): a theme of 8 of 9 studies restated the other themes' findings with their figures,
/// and a virtual focus group was stretched into a theme on safety mechanisms. Each theme now gets what earlier themes hold
/// of its sources, a broad theme is asked to compare, a repeated figure goes to the revision step, and a source that fits
/// only indirectly may be left out, with its reason on file.
/// </summary>
public class RedThreadTests
{
    private static ReviewEvidence Evidence() => new()
    {
        Sources = Enumerable.Range(1, 9).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}.", Findings = new[] { new EvidenceFinding($"F{r}.1", $"Finding {r}", $"Quote {r}.") } }).ToList(),
        Themes = new[]
        {
            new EvidenceTheme { Id = "T1", Name = "Plan reuse", Sources = new[] { 5, 7 }, Codes = new Dictionary<int, IReadOnlyList<string>> { [5] = new[] { "plan reuse cuts cost by 34%" }, [7] = new[] { "MASON adds latency" } } },
            new EvidenceTheme { Id = "T2", Name = "Evaluation", Sources = new[] { 1, 2, 3, 4, 5, 6, 7, 8 } },
            new EvidenceTheme { Id = "T3", Name = "Focus groups", Sources = new[] { 8, 9 } },
        },
    };

    [Fact]
    public void ALaterThemeGetsTheCodesThatPutItsSourcesIntoEarlierThemes()
    {
        var evidence = Evidence();
        Assert.Equal("", ReviewWriter.EarlierThemesEvidence(evidence, evidence.Themes[0]));
        Assert.Equal("Under \"Plan reuse\": [5] plan reuse cuts cost by 34%\nUnder \"Plan reuse\": [7] MASON adds latency",
            ReviewWriter.EarlierThemesEvidence(evidence, evidence.Themes[1]));
        Assert.Equal("", ReviewWriter.EarlierThemesEvidence(evidence, evidence.Themes[2])); // the evaluation theme gave no codes for [8]

        string block = ReviewWriter.EarlierThemesBlock(evidence, evidence.Themes[1], WritingProfile.Systematic);
        Assert.Contains("UNTRUSTED", block);
        Assert.Contains("[5] plan reuse cuts cost by 34%", block);
    }

    [Fact]
    public void ABroadThemeIsAskedToCompareAndEveryThemeMayLeaveOutASourceThatDoesNotFit()
    {
        var evidence = Evidence();
        Assert.True(ReviewWriter.IsBroadTheme(evidence, evidence.Themes[1]));
        Assert.False(ReviewWriter.IsBroadTheme(evidence, evidence.Themes[0]));

        string broad = ReviewWriter.RedThreadRule(WritingProfile.Systematic, evidence, evidence.Themes[1]);
        Assert.StartsWith("A finding the earlier themes report (listed above) is written up there", broad);
        Assert.Contains("This theme holds 8 of the review's 9 studies, so compare how the studies evaluate or approach the question", broad);
        Assert.Contains("Never stretch a study to fit the theme", broad);
        Assert.StartsWith("Never stretch a study", ReviewWriter.RedThreadRule(WritingProfile.Systematic, evidence, evidence.Themes[0]));
        // Issue #745: the rule holds for each finding, not only for each study.
        Assert.Contains("when one finding of a study fits the theme, its other results (scores or outcomes in its own field or task) do not come with it",
            ReviewWriter.RedThreadRule(WritingProfile.Systematic, evidence, evidence.Themes[0]));
    }

    [Fact]
    public void ASourceLeftOutMustBelongToTheThemeBeUncitedAndHaveAReason()
    {
        var sources = new[] { 8, 9 };
        Assert.Null(ReviewWriter.LeftOutProblem(new (int, string?)[] { (9, "a focus group study, not a safety mechanism") }, "Placement is studied [8].", sources));
        Assert.Contains("not a source of this theme", ReviewWriter.LeftOutProblem(new (int, string?)[] { (4, "it is about something else") }, "Placement [8].", sources));
        Assert.Contains("but the text cites it", ReviewWriter.LeftOutProblem(new (int, string?)[] { (8, "it is about something else") }, "Placement [8].", sources));
        Assert.Contains("gives no reason", ReviewWriter.LeftOutProblem(new (int, string?)[] { (9, "off topic") }, "Placement [8].", sources));
    }

    [Fact]
    public void TheSameResultFigureForTheSameSourceInALaterThemeIsARepeat()
    {
        var sections = new List<SynthesisSection>
        {
            new() { Field = "synthesis-1", Heading = "Plan reuse", Text = "Reusing plans cut the cost of a run by 34 % [5]." },
            new() { Field = "synthesis-2", Heading = "Evaluation", Text = "Cost fell by 34 percent when plans were cached [5]. Latency rose by 34 % in another setting [6]." },
        };
        var repeat = Assert.Single(ReviewWriter.RepeatedClaims(sections));
        Assert.Equal(("synthesis-2", "Cost fell by 34 percent when plans were cached [5].", "Plan reuse"), repeat);
    }

    [Fact]
    public async Task ASourceItsWriterLeavesOutGetsNoFillPassAndItsReasonIsOnFile()
    {
        var evidence = Evidence();
        var model = new FakeChatService()
            .Returns("""{"heading":"x","text":"Service placement is studied with simulations [8].","leftOut":[{"ref":9,"reason":"a virtual focus group, which bears on safety only indirectly"}]}""");

        var (section, coverage) = await ReviewWriter.WriteThemeAsync(model, WritingProfile.Systematic, "Map agent safety", evidence, evidence.Themes[2], 3, referenceCount: 9);

        Assert.Single(model.Prompts); // no fill pass for [9]
        Assert.Contains("7. Never stretch a study to fit the theme", model.Prompts[0]);
        Assert.Contains("\"leftOut\":[]", model.Prompts[0]);
        Assert.Equal("Service placement is studied with simulations [8].", section!.Text);
        Assert.Equal(new[] { 9 }, coverage.NotCited);
        Assert.Equal("not needed", coverage.FillPass);
        var left = Assert.Single(coverage.LeftOut!);
        Assert.Equal(9, left.Reference);

        Assert.Equal("left out of \"Focus groups\" by the writer of that theme, because it bears on the theme only indirectly: a virtual focus group, which bears on safety only indirectly",
            ThemeCoverage.LeftOutReason(new[] { coverage }, 9, id => evidence.Themes.First(t => t.Id == id).Name));
        Assert.Null(ThemeCoverage.LeftOutReason(new[] { coverage }, 8, id => id));
    }

    [Fact]
    public async Task ALaterThemesPromptCarriesTheEarlierThemesCodesAndTheClauseRule()
    {
        var evidence = Evidence();
        var model = new FakeChatService().Returns("""{"heading":"x","text":"The studies evaluate agents in simulations [1, 2] and in deployments [3, 4]. As reported under plan reuse, caching plans saves cost [5]. Latency is measured too [6, 7]. One study places services [8].","leftOut":[]}""");

        await ReviewWriter.WriteThemeAsync(model, WritingProfile.Systematic, "Map agent safety", evidence, evidence.Themes[1], 2, referenceCount: 9);

        Assert.Contains("Under \"Plan reuse\": [5] plan reuse cuts cost by 34%", model.Prompts[0]);
        Assert.Contains("refer to it briefly by that theme's name", model.Prompts[0]);
        Assert.Contains("never both in one clause", model.Prompts[0]);
        Assert.Contains("8. Never open a sentence with a numeral", model.Prompts[0]);
    }

    [Fact]
    public async Task AnotherThemeNamedWithItsCapitalsIsSentBackAndLoweredInThePrompt()
    {
        // Issue #746: "within Architectural and Agent-Level Safety Mechanisms" in running text.
        var evidence = Evidence();
        string head = "The studies evaluate agents in simulations [1, 2] and in deployments [3]. ";
        string tail = " caching plans saves cost [5]. Latency is measured too [6, 7]. One study places services [8].";
        var model = new FakeChatService()
            .Returns("{\"heading\":\"x\",\"text\":\"" + head + "Within Plan reuse," + tail + "\",\"leftOut\":[]}")
            .Returns("{\"heading\":\"x\",\"text\":\"" + head + "Within plan reuse," + tail + "\",\"leftOut\":[]}");

        var (section, _) = await ReviewWriter.WriteThemeAsync(model, WritingProfile.Systematic, "Map agent safety", evidence, evidence.Themes[1], 2, referenceCount: 9);

        Assert.Contains("do not cover them here): plan reuse", model.Prompts[0]);
        Assert.Contains("names another theme with its capitals", model.Prompts[1]);
        Assert.Contains("Within plan reuse,", section!.Text);
    }
}

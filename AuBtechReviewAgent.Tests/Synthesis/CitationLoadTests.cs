using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The rule on references per sentence (Lau, 25 November 2026; the citation dumps of issue #464): a written sentence
/// cites at most three references across all its markers, ideally one or two. It is in every writing prompt and checked
/// in code; a sentence that breaks it sends the answer back, a revision may not add one, and the repair never adds a reference.
/// </summary>
public class CitationLoadTests
{
    [Fact]
    public void ThreeReferencesAreAcceptedAndFourAreSentBack()
    {
        Assert.Null(ReviewWriter.CitationLoadProblem("Agents log their tool calls [1, 2, 3]. Loops recover [4]."));
        string? problem = ReviewWriter.CitationLoadProblem("Agents log their tool calls [1, 2, 3, 4]. Loops recover [5].");
        Assert.NotNull(problem);
        Assert.Contains("cites 4 references ([1], [2], [3], [4]); a sentence cites at most 3, ideally one or two", problem);
    }

    [Fact]
    public void EveryMarkerInASentenceCountsAndARangeCountsEachReference()
    {
        Assert.Contains("cites 4 references", ReviewWriter.CitationLoadProblem("One logs calls [1, 2], while another audits them [3] and a third replays them [4].")!);
        Assert.Contains("cites 4 references", ReviewWriter.CitationLoadProblem("Several report audits [2-5].")!);
        // The same reference twice in one sentence counts once.
        Assert.Null(ReviewWriter.CitationLoadProblem("One logs calls [1], audits them [1, 2] and replays them [3]."));
    }

    [Fact]
    public void ASentenceAlreadyInTheTextARevisionStartsFromIsNotHeldAgainstIt()
    {
        const string Before = "Agents log their tool calls [1, 2, 3, 4].";
        Assert.Null(ReviewWriter.CitationLoadProblem(Before + " Loops recover [5].", Before));
        Assert.NotNull(ReviewWriter.CitationLoadProblem("Agents log every call [1, 2, 3, 4].", Before));
        Assert.Contains("the revision breaks the rule on references per sentence", ThematicSynthesis.RevisionProblem("Logs [1]. Audits [2, 3, 4].", "Logs and audits [1, 2, 3, 4].", 4)!);
    }

    [Fact]
    public void ThePromptRuleNamesTheNumberCodeChecks()
    {
        string rule = ReviewWriter.CitationLoadRule(WritingProfile.Systematic);
        Assert.Contains("A sentence cites at most 3 studies in all its markers together, and ideally one or two.", rule);
        Assert.Contains(rule, ReviewWriter.ClaimPerCitationRule(WritingProfile.Systematic));
    }

    [Fact]
    public async Task ASubsectionWithAFourReferenceSentenceIsSentBackAndTheSendBackIsOnFile()
    {
        var evidence = ThemeShapeTests.Evidence(3);
        var model = new FakeChatService()
            .Returns("""{"heading":"Logs","text":"Agents log every tool call [1, 2, 3, 4]."}""")
            .Returns("""{"heading":"Logs","text":"Agents log every tool call [1, 2], and replay them [3]."}""");

        var (section, coverage) = await ReviewWriter.WriteThemeAsync(model, WritingProfile.Systematic, "Map logging", evidence, evidence.Themes[0], 1, referenceCount: 4);

        Assert.Equal("Agents log every tool call [1, 2], and replay them [3].", section!.Text);
        Assert.Contains("cites 4 references", model.Prompts[1]);
        Assert.Contains(coverage.WritingNotes!, n => n.StartsWith("The subsection was sent back for citing too many references in one sentence"));
    }

    [Fact]
    public void AClosingPartThatCitesFourReferencesInASentenceIsSentBack()
    {
        string? problem = ReviewWriter.ClosingProblem("The evidence rests on surveys [1, 2, 3, 4].", "Teams should log calls.", "Trials are missing.", "Logging matters.", "", 4);
        Assert.StartsWith("limitations: the sentence \"The evidence rests on surveys [1, 2, 3, 4].\" cites 4 references", problem);
        Assert.Null(ReviewWriter.ClosingProblem("The evidence rests on surveys [1, 2, 3].", "Teams should log calls.", "Trials are missing.", "Logging matters.", "", 4));
    }

    [Fact]
    public async Task ARationaleSentenceWithFourReferencesIsSentBackAndOnFile()
    {
        static string Front(string rationale) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            { ["title"] = "Agent memory: a systematic review", ["abstract"] = "We read the studies.", ["rationale"] = rationale, ["objectives"] = "To map agent memory." });
        var model = new FakeChatService().Returns(Front("Agents keep a running summary [1], [2], [3] and [4].")).Returns(Front("Agents keep a running summary [1, 2]."));
        var notes = new List<string>();

        var front = await ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent memory", "Map agent memory", "Studies: 4.",
            new[] { new SynthesisSection { Field = "synthesis-1", Heading = "Memory", Text = "Agents keep a running summary [1, 2]." } }, "", "[1] A. [2] B. [3] C. [4] D.", 4, notes.Add);

        Assert.Equal("Agents keep a running summary [1, 2].", front!.Rationale);
        Assert.Contains("YOUR PREVIOUS ANSWER WAS REJECTED: the rationale: the sentence", model.Prompts[1]);
        Assert.Contains("cite at most 3 studies each, ideally one or two", model.Prompts[0]);
        Assert.StartsWith("The front-matter call was sent back for citing too many references in one sentence", Assert.Single(notes));
    }

    [Fact]
    public async Task TheRepairNeverAddsAFourthReference()
    {
        const string Sentence = "Agents log every tool call [1, 2, 3].";
        var chat = new FakeChatService().RespondsWith(_ => """{"repairs":[{"id":1,"action":"rewrite","sentence":"Agents log tool calls [1, 2, 3, 4].","reason":"widened"}]}""");
        var flagged = new CitationSupportResult { Field = "synthesis-1", SentenceIndex = 0, Sentence = Sentence, Reference = 1, Verdict = CitationSupportChecker.Partial, Reason = "only some calls" };
        var papers = Enumerable.Range(1, 4).Select(r => new ReferencedPaper(r, $"p{r}", $"Paper {r}", "Agents log tool calls.", Array.Empty<DocumentChunk>())).ToList();

        var (fields, _, repairs) = await CitationRepairer.RepairAsync(chat, new() { ("synthesis-1", Sentence) }, new() { flagged }, papers,
            new Dictionary<int, IReadOnlyList<string>>(), referenceCount: 4);

        Assert.Equal(Sentence, Assert.Single(fields).Text);
        var repair = Assert.Single(repairs);
        Assert.Equal("rejected", repair.Action);
        Assert.Contains("added citations 4", repair.Note);
        Assert.Contains(chat.Prompts, p => p.Contains("never add a new number; a sentence cites at most 3"));
    }

    [Fact]
    public void ASoftGuardSendsBackTwiceThenKeepsTheAnswerWithANote()
    {
        var notes = new List<string>();
        var guard = ReviewWriter.WithSoftGuard<string>(_ => null, _ => "it lists", "subsection", "a list", notes.Add);
        Assert.Equal("it lists", guard("a"));
        Assert.Equal("it lists", guard("a"));
        Assert.Null(guard("a"));
        Assert.Equal(new[] { "The subsection was sent back for a list (1 of 2): it lists", "The subsection was sent back for a list (2 of 2): it lists", "The subsection was kept after 2 send-backs with a list: it lists" }, notes);
    }

    [Fact]
    public void SoftChecksShareOneBudgetAndHardOnesAlwaysSendBack()
    {
        var notes = new List<string>();
        var guard = ReviewWriter.WithSoftGuards<string>(a => a == "hard" ? "hard problem" : null, "step", notes.Add, 2,
            (a => "first problem", "the first"), (a => "second problem", "the second"));
        Assert.Equal("first problem", guard("x"));
        Assert.Equal("first problem", guard("x"));
        Assert.Null(guard("x")); // the budget of two is shared: the second check does not get two more
        Assert.Equal("hard problem", guard("hard"));
        Assert.Equal(new[] { "The step was sent back for the first (1 of 2): first problem", "The step was sent back for the first (2 of 2): first problem",
            "The step was kept after 2 send-backs with the first: first problem", "The step was kept after 2 send-backs with the second: second problem" }, notes);
    }
}

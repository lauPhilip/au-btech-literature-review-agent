using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Pins down the parts of the citation support check the other tests leave out: how answers are read, the
/// verdicts for a missing reference, a failed call, a missing answer and a claim the full text does not mention,
/// the order of the results, the cache, the summary sentence and the marker split.
/// </summary>
public class CitationCheckDetailsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "check-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Abstract = "Agent loops recover from faults through supervisor checks in production.";
    private const string Quote = "Agent loops recover from faults through supervisor checks";

    private static ReferencedPaper Paper(int n, string abstractText = Abstract, params string[] chunks) =>
        new(n, $"P{n}", $"Paper {n}", abstractText, chunks.Select((c, i) => new DocumentChunk($"P{n}", i + 1, c)).ToList());

    private static CitedSentence Cited(string field, int index, string sentence) =>
        new(field, index, sentence, CitationSupportChecker.ReferencesIn(sentence));

    [Fact]
    public void AnswersAreReadFromTheFirstObjectWithLooseSentenceNumbers()
    {
        var map = CitationSupportChecker.ParseVerdicts("""Here you go: {"results":[{"sentence":"S2","verdict":"Partially Supported","excerpt":" E1 ","quote":" q ","reason":" r "},{"sentence":0,"verdict":"supported"}]}""");
        Assert.Equal(new[] { 2 }, map.Keys);
        Assert.Equal(new CitationSupportChecker.ParsedVerdict("partially_supported", "E1", "q", "r"), map[2]);
        Assert.Empty(CitationSupportChecker.ParseVerdicts("no json here"));
        Assert.Empty(CitationSupportChecker.ParseVerdicts("""{"other":[]}"""));
    }

    [Fact]
    public async Task AReferenceOutsideTheListIsUnverifiableWithoutAModelCall()
    {
        var chat = new FakeChatService();
        var results = await CitationSupportChecker.CheckAsync(chat, new[] { Cited("discussionItem", 0, "Loops recover from faults quickly [9].") }, new[] { Paper(1) });

        var r = Assert.Single(results);
        Assert.Equal((CitationSupportChecker.Unverifiable, "none", "The reference number is not in the reference list.", 9), (r.Verdict, r.EvidenceBasis, r.Reason, r.Reference));
        Assert.Equal("Loops recover from faults quickly", r.AttributedText);
        Assert.Equal(CitationSupportChecker.Unverifiable, r.FirstVerdict);
        Assert.Empty(chat.Prompts);
    }

    [Fact]
    public async Task AFailedCallAndAMissingAnswerAreUnverifiable()
    {
        var sentences = new[] { Cited("synthesis-1", 0, "Agent loops recover from faults [1]."), Cited("synthesis-1", 1, "Supervisors check every step [1].") };

        var failed = await CitationSupportChecker.CheckAsync(new FakeChatService().Throws(new InvalidOperationException("down")), sentences, new[] { Paper(1) });
        Assert.All(failed, r => Assert.Equal((CitationSupportChecker.Unverifiable, "abstract only", "Check failed: InvalidOperationException."), (r.Verdict, r.EvidenceBasis, r.Reason)));

        var partial = await CitationSupportChecker.CheckAsync(new FakeChatService().Returns(
            $$"""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"{{Quote}}","reason":"ok"}]}"""), sentences, new[] { Paper(1) });
        Assert.Equal(CitationSupportChecker.Supported, partial[0].Verdict);
        Assert.Equal(("E1 (abstract)", true), (partial[0].EvidenceLocation, partial[0].QuoteVerified));
        Assert.Equal((CitationSupportChecker.Unverifiable, "The model gave no verdict for this sentence."), (partial[1].Verdict, partial[1].Reason));
    }

    [Fact]
    public async Task AClaimTheFullTextDoesNotMentionIsNotSupported()
    {
        var paper = Paper(1, Abstract, "The study measured latency in three data centres.");
        var results = await CitationSupportChecker.CheckAsync(new FakeChatService().Returns(
            """{"results":[{"sentence":1,"verdict":"not in excerpts","excerpt":"","quote":"","reason":"Not there."}]}"""),
            new[] { Cited("synthesis-1", 0, "Agents negotiate prices with each other [1].") }, new[] { paper });

        var r = Assert.Single(results);
        Assert.Equal((CitationSupportChecker.NotSupported, "full text", "The excerpts do not mention this. Not there."), (r.Verdict, r.EvidenceBasis, r.Reason));
    }

    [Fact]
    public async Task VerifiedFindingsAreShownAsExcerptsAndResultsComeBackInOrder()
    {
        var chat = new FakeChatService().RespondsWith(prompt => prompt.Contains("CITED PAPER [2]")
            ? """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E2","quote":"Governance layers audit every agent action in production","reason":"ok"}]}"""
            : """{"results":[{"sentence":1,"verdict":"not_supported","excerpt":"E1","quote":"","reason":"No."},{"sentence":2,"verdict":"not_supported","excerpt":"E1","quote":"","reason":"No."}]}""");
        var findings = new Dictionary<int, IReadOnlyList<string>> { [2] = new[] { "Governance layers audit every agent action in production.", " " } };
        var sentences = new[]
        {
            Cited("synthesis-2", 0, "Governance layers audit agent actions [2] and loops recover [1]."),
            Cited("discussionItem", 3, "Loops recover from every fault [1]."),
        };

        var results = await CitationSupportChecker.CheckAsync(chat, sentences, new[] { Paper(1), Paper(2, "An abstract.") }, verifiedFindings: findings);

        Assert.Equal(new[] { ("discussionItem", 3, 1), ("synthesis-2", 0, 1), ("synthesis-2", 0, 2) }, results.Select(r => (r.Field, r.SentenceIndex, r.Reference)));
        var second = results[2];
        Assert.Equal((CitationSupportChecker.Supported, "E2 (verified extraction quote)", true), (second.Verdict, second.EvidenceLocation, second.QuoteVerified));
        string prompt = chat.Prompts.Single(p => p.Contains("CITED PAPER [2]"));
        Assert.Contains("E2: Governance layers audit every agent action in production.", prompt);
        Assert.DoesNotContain("E3:", prompt);
    }

    [Fact]
    public async Task ACachedAnswerIsReusedUnderTheWholePromptAndTheModel()
    {
        var store = new CheckCache(new ReviewCache(new CacheOptions { Folder = Path.Join(_root, "cache") }, _root), "mistral-large-latest");
        var sentences = new[] { Cited("synthesis-1", 0, "Agent loops recover from faults [1].") };
        var first = new FakeChatService().Returns($$"""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"{{Quote}}","reason":"ok"}]}""");

        await CitationSupportChecker.CheckAsync(first, sentences, new[] { Paper(1) }, cache: store);
        string key = ReviewCache.Key("citation-check-v1", "mistral-large-latest", first.Prompts.Single());
        Assert.True(store.Cache.TryGet<CitationSupportChecker.CachedAnswer>("citation-check", key, out var hit));

        var again = await CitationSupportChecker.CheckAsync(new FakeChatService().Throws(new InvalidOperationException()), sentences, new[] { Paper(1) }, cache: store);
        Assert.Equal(CitationSupportChecker.Supported, again.Single().Verdict);
        Assert.Contains("\"supported\"", hit!.Text);
    }

    [Fact]
    public void TheSummarySentenceNamesEveryCount()
    {
        var results = new[]
        {
            new CitationSupportResult { Verdict = CitationSupportChecker.Supported, FirstVerdict = CitationSupportChecker.Partial, SecondVerdict = CitationSupportChecker.Supported, ResolvedBy = "second check" },
            new CitationSupportResult { Verdict = CitationSupportChecker.Partial, FirstVerdict = CitationSupportChecker.Partial },
            new CitationSupportResult { Verdict = CitationSupportChecker.InsufficientEvidence, FirstVerdict = CitationSupportChecker.InsufficientEvidence },
        };
        var summary = CitationSupportSummary.From(results);
        Assert.Equal((3, 1, 1, 0, 1, 0, 1, 0, 1), (summary.Checked, summary.Supported, summary.PartiallySupported, summary.NotSupported, summary.InsufficientEvidence,
            summary.Unverifiable, summary.SecondChecked, summary.SecondCheckAgreed, summary.UpgradedBySecondCheck));
        Assert.Equal("An automated check compared the part of each cited sentence attributed to a paper with the text of that paper. Of 3 citations checked, " +
            "1 was judged supported, 1 partially supported, 0 not supported, 1 could not be judged because only the abstract was available and it does not mention the claim, " +
            "and 0 could not be verified. 1 citation that was not fully supported went through a second, independent check with more of the paper's text; it agreed with the first check on 0 " +
            "and found verbatim support that raised the verdict for 1. Every verdict, with the quoted evidence, is listed in citation-audit.json; these are model judgements and should be spot-checked by a human reviewer.",
            summary.ToSentence());
        Assert.Equal("No inline citations were available for the automated support check.", new CitationSupportSummary().ToSentence());
    }

    [Fact]
    public void TheSummaryCountsRemovedCitationsOnTheirOwn()
    {
        var checks = new[] { new CitationSupportResult { Verdict = CitationSupportChecker.Supported, FirstVerdict = CitationSupportChecker.Supported } };

        var summary = CitationSupportSummary.From(checks, removed: 2);
        string sentence = summary.ToSentence();

        Assert.Equal((1, 1, 2), (summary.Checked, summary.Supported, summary.Removed));
        Assert.Contains("Of 1 citation checked, 1 was judged supported", sentence);
        Assert.Contains(" 2 citations that were still not supported after the repair were removed, with their claim when no other citation backed it; they are not in the counts above and are listed under RemovedCitations in citation-audit.json.", sentence);
        Assert.Contains("1 citation that was still not supported after the repair was removed", CitationSupportSummary.From(checks, 1).ToSentence());
        Assert.DoesNotContain("removed", CitationSupportSummary.From(checks).ToSentence());
        Assert.StartsWith("An automated check", CitationSupportSummary.From(Array.Empty<CitationSupportResult>(), 1).ToSentence());
    }

    [Fact]
    public void MarkersAreSplitFromTheTextInOrder()
    {
        Assert.Equal(new[] { ("A ", false), ("[1]", true), (" b ", false), ("[2, 3]", true), (".", false) }, CitationSupportChecker.SplitMarkers("A [1] b [2, 3]."));
        Assert.Equal(new[] { 2, 3, 4, 7 }, CitationSupportChecker.ReferencesIn("Shown [2-4] and [7]."));
        Assert.Equal((2, 1, 0), (CitationSupportChecker.Rank(CitationSupportChecker.Supported), CitationSupportChecker.Rank(CitationSupportChecker.Partial), CitationSupportChecker.Rank(CitationSupportChecker.NotSupported)));
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Pins down what RunMetrics.Build computes from a run's ledger, report, citation audit and call log, so the
/// figures on the Metrics page stay the same when the class is split.
/// </summary>
public class RunMetricsBuildTests
{
    private static readonly DateTime Started = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ReviewState State() => new()
    {
        SearchQuery = "agent loops",
        Timestamp = Started,
        CompletedUtc = Started.AddMinutes(12.5),
        RunSettings = new RunSettingsRecord { Model = "mistral-large-latest", AppVersion = "0.3.0+abcdef1234" },
        Stats = new ReviewStats
        {
            TotalIdentified = 10, DuplicatesRemoved = 2, Screened = 8, Included = 4, ScreeningKappa = 0.8,
            UncertainDecisions = 1, FullTextRetrieved = 3, StudiesCitedInSynthesis = 3, CitationsRepaired = 2, CitationsOutsideTheme = 1, CitationsRemoved = 4,
        },
        SynthesizedRecords = Enumerable.Range(1, 4).Select(n => new IncludedPaperMetricRow { ReferenceNumber = n }).ToList(),
        Extractions = new List<StudyExtraction>
        {
            new()
            {
                KeyFindings = new()
                {
                    new EvidencedValue { Value = "A", QuoteVerified = true },
                    new EvidencedValue { Value = "B" },
                    new EvidencedValue { Value = "Not reported" },
                },
            },
            new() { Error = "Extraction failed: x", KeyFindings = new() { new EvidencedValue { Value = "C", QuoteVerified = true } } },
        },
    };

    private static PrismaReport Report() => new()
    {
        SynthesisResultsItem = "Overview text here [1]. [MERMAID_START]graph TD; A-->B[MERMAID_END]",
        SynthesisSections = new() { new SynthesisSection { Field = "synthesis-1", Text = "Agents fail [1, 2]. Loops help." } },
        DiscussionItem = "Safety matters [2].",
    };

    private static List<CitationSupportResult> Checks() => new()
    {
        new() { Field = "synthesis-1", Sentence = "Agents fail [1, 2].", Verdict = CitationSupportChecker.Supported, FirstVerdict = CitationSupportChecker.Supported, EvidenceBasis = "full text" },
        new()
        {
            Field = "synthesis-1", Sentence = "Agents fail [1, 2].", Verdict = CitationSupportChecker.Supported, FirstVerdict = CitationSupportChecker.Partial,
            SecondVerdict = CitationSupportChecker.Supported, ResolvedBy = "second check", EvidenceBasis = "abstract only",
        },
        new()
        {
            Field = "discussionItem", Sentence = "Safety matters [2].", Verdict = CitationSupportChecker.NotSupported, FirstVerdict = CitationSupportChecker.NotSupported,
            SecondVerdict = CitationSupportChecker.NotSupported,
        },
        new() { Field = "conclusionItem", Sentence = "A [1] B [2] C [3] D [4] E [5].", Verdict = CitationSupportChecker.InsufficientEvidence, FirstVerdict = CitationSupportChecker.InsufficientEvidence, EvidenceBasis = "abstract only" },
    };

    private static List<LlmCallRecord> Calls() => new()
    {
        new() { Stage = "screening", Attempts = 2, Outcome = "ok", Usage = """{"total_tokens":100}""", DurationMs = 1500 },
        new() { Stage = "extraction", Attempts = 1, Outcome = "failed", DurationMs = 300 },
        new() { Stage = "screening", Attempts = 1, Outcome = "ok", Usage = """{"prompt_tokens":5,"completion_tokens":5}""", DurationMs = 1000 },
    };

    private static readonly Dictionary<string, string> Settings = new() { ["B"] = "2", ["A"] = "1" };

    private static RunMetrics Build(ReviewState state, PrismaReport? report, CitationSupportSummary? initial = null) =>
        RunMetrics.Build(new Guid("0123456789abcdef0123456789abcdef"), state, report, Checks(), initial, 5, Calls(), Settings);

    [Fact]
    public void TheHeaderSearchAndExtractionFiguresComeFromTheLedger()
    {
        var m = Build(State(), Report());
        Assert.Equal("0123456789abcdef0123456789abcdef", m.RunId);
        Assert.Equal("2026-01-01T10:12:30Z", m.CompletedUtc);
        Assert.Equal("0.3.0+abcdef1234", m.AppVersion);
        Assert.Equal("mistral-large-latest", m.Model);
        Assert.Equal(RunMetrics.Sha("agent loops"), m.QueryHash);
        Assert.Equal(12, m.QueryHash.Length);
        Assert.Equal(RunMetrics.Sha("mistral-large-latest|0.3.0+abcdef1234|A=1|B=2"), m.ConfigFingerprint);
        Assert.Equal("1", m.Settings["A"]);
        Assert.Equal(12.5, m.DurationMinutes);

        Assert.Equal((10, 2, 8, 4, (double?)0.8, 1, 3, 0.75), (m.Identified, m.DuplicatesRemoved, m.Screened, m.Included, m.ScreeningKappa, m.UncertainDecisions, m.FullTextRetrieved, m.FullTextShare));
        Assert.Equal((2, 0.5, 1), (m.KeyFindings, m.VerifiedFindingShare, m.ExtractionErrors));
        Assert.Equal((3, 0.75, 2, 1, 5), (m.StudiesCited, m.CoverageShare, m.Repaired, m.OutsideTheme, m.InvalidMarkersStripped));
        // Citations the second repair pass removed are a field of their own (issue #234); a file written before it reads as 0.
        Assert.Equal(4, m.Removed);
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<RunMetrics>("""{"RunId":"old","Repaired":2}""")!.Removed);
    }

    [Fact]
    public void TheCitationFiguresAreCountedFromTheAudit()
    {
        var initial = new CitationSupportSummary { Checked = 4, Supported = 1, PartiallySupported = 1, NotSupported = 1, InsufficientEvidence = 1 };
        var m = Build(State(), Report(), initial);

        Assert.Equal((4, 2, 0, 1, 1, 0), (m.CitationsFinal.Total, m.CitationsFinal.Supported, m.CitationsFinal.PartiallySupported, m.CitationsFinal.NotSupported, m.CitationsFinal.InsufficientEvidence, m.CitationsFinal.Unverifiable));
        Assert.Equal((4, 1, 1, 1, 1, 0), (m.CitationsInitial.Total, m.CitationsInitial.Supported, m.CitationsInitial.PartiallySupported, m.CitationsInitial.NotSupported, m.CitationsInitial.InsufficientEvidence, m.CitationsInitial.Unverifiable));
        Assert.Equal(0.5, m.CitationsFinal.SupportedShare);

        Assert.Equal(new[] { "synthesis", "discussion", "conclusionItem" }, m.CitationsBySection.Keys);
        Assert.Equal(2, m.CitationsBySection["synthesis"].Supported);
        Assert.Equal(new[] { "full text", "abstract only", "none" }, m.CitationsByEvidence.Keys);
        Assert.Equal(2, m.CitationsByEvidence["abstract only"].Total);
        Assert.Equal(new[] { "1", "2", "4+" }, m.CitationsByPapersInSentence.Keys);
        Assert.Equal((1, 2, 1), (m.CitationsByPapersInSentence["1"].Total, m.CitationsByPapersInSentence["2"].Total, m.CitationsByPapersInSentence["4+"].Total));
        Assert.Equal((2, 1, 1), (m.SecondChecked, m.SecondCheckAgreed, m.UpgradedBySecondCheck));

        Assert.Equal(0, Build(State(), Report()).CitationsInitial.Total);
    }

    [Fact]
    public void TheTextFiguresLeaveTheDiagramOut()
    {
        var m = Build(State(), Report());
        Assert.Equal((10, 3, 4), (m.SynthesisWords, m.DiscussionWords, m.Sentences));
        Assert.Equal((0.75, 0.3333, 30.77), (m.CitedSentenceShare, m.MultiPaperSentenceShare, m.CitationsPer100Words));

        var none = Build(State(), null);
        Assert.Equal((0, 0, 0, 0.0), (none.SynthesisWords, none.DiscussionWords, none.Sentences, none.CitationsPer100Words));
        Assert.False(none.ThematicSynthesisUsed);
    }

    [Fact]
    public void TheCostIsAddedUpByStage()
    {
        var m = Build(State(), Report());
        Assert.Equal((3, 1, 1, 110L), (m.LlmCalls, m.LlmRetries, m.LlmFailures, m.Tokens));
        Assert.Equal(new[] { "extraction", "screening" }, m.CostByStage.Keys);
        Assert.Equal((2, 2.5, 110L), (m.CostByStage["screening"].Calls, m.CostByStage["screening"].Seconds, m.CostByStage["screening"].Tokens));
        Assert.Equal((1, 0.3, 0L), (m.CostByStage["extraction"].Calls, m.CostByStage["extraction"].Seconds, m.CostByStage["extraction"].Tokens));
        // Issue #414: input and output apart (the first call reported only a total), and the failed call with no usage.
        Assert.Equal((5L, 5L, 0L, 1), (m.InputTokens, m.OutputTokens, m.ReasoningTokens, m.CallsWithoutUsage));
        Assert.Equal((1, 1), (m.CostByStage["screening"].Retries, m.CostByStage["extraction"].Failures));
        Assert.Equal(new[] { "unknown" }, m.CostByModel!.Keys);
        Assert.Equal("systematic", m.ReviewType);
    }

    [Fact]
    public void TheCodebookFiguresCountThemesOnlyWhenTheThemesWereWritten()
    {
        var state = State();
        state.ThematicSynthesis = new ThematicCodebook
        {
            Codes = new() { new() { Anchor = "verified finding" }, new() { Anchor = "unanchored" }, new() { Anchor = "verified quote" }, new() { Anchor = "verified finding" } },
            Themes = new() { new() { Studies = new() { 1, 2 } }, new() { Studies = new() { 2, 3 } } },
            Uncoded = new() { new UncodedStudy(4, "no verified finding") },
            SecondCoding = new SecondCoding { Kappa = 0.6 },
            FallbackReason = "fell back",
        };

        var m = Build(state, Report());
        Assert.True(m.ThematicSynthesisUsed);
        Assert.Equal((2, 4, 0.75, 3, 1, (double?)0.6, "fell back"), (m.Themes, m.Codes, m.AnchoredCodeShare, m.StudiesCoded, m.StudiesUncoded, m.CodingKappa, m.FallbackReason));

        var notWritten = Build(state, new PrismaReport());
        Assert.False(notWritten.ThematicSynthesisUsed);
        Assert.Equal((0, 4), (notWritten.Themes, notWritten.Codes));
    }

    [Fact]
    public void ARunWithoutSettingsOrCompletionTimeStillBuilds()
    {
        var state = new ReviewState { SearchQuery = "q" };
        var m = RunMetrics.Build(Guid.Empty, state, null, Array.Empty<CitationSupportResult>(), null, 0, Array.Empty<LlmCallRecord>(), new Dictionary<string, string>());
        Assert.Equal(RecordingChatCompletionService.AppVersion, m.AppVersion);
        Assert.Equal("", m.Model);
        Assert.Equal(0, m.DurationMinutes);
        Assert.Equal((0.0, 0.0, 0.0), (m.FullTextShare, m.VerifiedFindingShare, m.CoverageShare));
        Assert.Empty(m.CostByStage);
        Assert.False(m.IsMultivocal);
        Assert.True(new RunMetrics { Settings = new() { ["Module"] = "multivocal" } }.IsMultivocal);
    }
}

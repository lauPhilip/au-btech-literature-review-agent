using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Sentences whose citation the check did not confirm (not supported, or could not be judged) are marked in code with a
/// dagger and a footnote in main.tex, from the final verdicts of the text as it reads now.
/// </summary>
public class UnconfirmedCitationTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "unconfirmed-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static CitationSupportResult Check(string field, int sentence, string text, string verdict, int reference = 1) =>
        new() { Field = field, SentenceIndex = sentence, Sentence = text, Reference = reference, Verdict = verdict };

    [Fact]
    public void OnlyNotSupportedAndUnverifiableLeaveACitationUnconfirmed()
    {
        Assert.True(CitationMarkers.IsUnconfirmed(CitationSupportChecker.NotSupported));
        Assert.True(CitationMarkers.IsUnconfirmed(CitationSupportChecker.Unverifiable));
        Assert.False(CitationMarkers.IsUnconfirmed(CitationSupportChecker.Supported));
        Assert.False(CitationMarkers.IsUnconfirmed(CitationSupportChecker.Partial));
        Assert.False(CitationMarkers.IsUnconfirmed(CitationSupportChecker.InsufficientEvidence));
    }

    [Fact]
    public void EachMarkedSentenceGetsADaggerAndTheFirstOfASectionItsFootnote()
    {
        const string text = "Loops recover [1]. Audits help [2]. Both matter [1, 2].";
        var checks = new[]
        {
            Check("discussionItem", 0, "Loops recover [1].", CitationSupportChecker.NotSupported),
            Check("discussionItem", 1, "Audits help [2].", CitationSupportChecker.Supported, 2),
            Check("discussionItem", 2, "Both matter [1, 2].", CitationSupportChecker.Supported),
            Check("discussionItem", 2, "Both matter [1, 2].", CitationSupportChecker.Unverifiable, 2),
        };

        string marked = CitationMarkers.MarkSentences(text, CitationMarkers.Unconfirmed(checks)["discussionItem"]);

        Assert.Equal("Loops recover [1]. Audits help [2]. Both matter [1, 2].", marked);
        Assert.Equal(@"Loops recover~\cite{ref1}.{\renewcommand{\thefootnote}{\dag}\footnote{The automated check could not confirm this citation.}} Audits help~\cite{ref2}. Both matter~\cite{ref1,ref2}.{\textsuperscript{\dag}}",
            PaperLatex.Escape(marked));
    }

    [Fact]
    public void ASentenceThatChangedSinceTheCheckIsNotMarked()
    {
        var sentences = new Dictionary<int, string> { [0] = "Loops recover [1]." };
        Assert.Equal("Loops always recover [1].", CitationMarkers.MarkSentences("Loops always recover [1].", sentences));
    }

    [Fact]
    public void AReportWithoutUnconfirmedCitationsIsTheSameObject()
    {
        var report = new PrismaReport { DiscussionItem = "Loops recover [1].", CitationCheckSummary = "All checked." };
        Assert.Same(report, CitationMarkers.MarkUnconfirmed(report, new[] { Check("discussionItem", 0, "Loops recover [1].", CitationSupportChecker.Supported) }));
    }

    [Fact]
    public void TheMarkedReportSaysHowManySentencesAreMarkedAndLeavesTheSavedReportAlone()
    {
        var report = new PrismaReport
        {
            DiscussionItem = "Loops recover [1].",
            SynthesisResultsItem = "Audits help [2]. [MERMAID_START]graph TD[MERMAID_END]",
            CitationCheckSummary = "All checked.",
        };
        var checks = new[]
        {
            Check("discussionItem", 0, "Loops recover [1].", CitationSupportChecker.NotSupported),
            Check("synthesisResultsItem", 0, "Audits help [2].", CitationSupportChecker.Unverifiable, 2),
        };

        var marked = CitationMarkers.MarkUnconfirmed(report, checks);

        Assert.Equal("Loops recover [1].", marked.DiscussionItem);
        Assert.Equal("Audits help [2]. [MERMAID_START]graph TD[MERMAID_END]", marked.SynthesisResultsItem);
        Assert.Equal("All checked. 2 sentences whose citation the check did not confirm (judged not supported, or not judged) are marked with a dagger (†) and a footnote.", marked.CitationCheckSummary);
        Assert.Equal("Loops recover [1].", report.DiscussionItem);
    }

    [Fact]
    public async Task MainTexOfAFinishedRunMarksTheSentenceTheCheckDidNotConfirm()
    {
        var model = new FakeChatService().RespondsWith(ReviewWriterTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));
        string before = engine.MainTexFromDisk(runId)!;
        Assert.DoesNotContain(@"\thefootnote", before);

        // The check's final verdict on one sentence of the discussion is "not supported".
        string auditPath = Path.Join(_root, runId.ToString("N"), "citation-audit.json");
        var audit = JsonNode.Parse(File.ReadAllText(auditPath))!;
        var check = audit["SupportChecks"]!.AsArray().First(c => c!["Field"]!.GetValue<string>() == "discussionItem")!;
        check["Verdict"] = CitationSupportChecker.NotSupported;
        File.WriteAllText(auditPath, audit.ToJsonString());

        string after = engine.MainTexFromDisk(runId)!.Replace(LatexBreaks.Break, "");
        // main.tex cites by the keys of references.bib, so the sentence is compared up to its citation.
        string sentence = PaperLatex.Escape(check["Sentence"]!.GetValue<string>());
        string words = sentence[..sentence.IndexOf(@"~\cite{", StringComparison.Ordinal)];
        Assert.Matches(Regex.Escape(words) + @"~\\cite\{[^}]+\}\." + Regex.Escape(@"{\renewcommand{\thefootnote}{\dag}\footnote{The automated check could not confirm this citation.}}"), after);
        Assert.Contains("One sentence whose citation the check did not confirm", after);
    }
}

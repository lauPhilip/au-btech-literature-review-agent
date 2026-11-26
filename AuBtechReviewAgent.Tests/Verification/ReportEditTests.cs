using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The checked editor's backend on a complete offline systematic run: an edit is checked again on the fast check and
/// kept with its verdicts, an unsupported citation is kept and marked, code-written parts and other browsers are
/// refused, undo restores the earlier text, and main.tex is built from the edited text.
/// </summary>
public class ReportEditTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "edit-" + Guid.NewGuid().ToString("N"));
    private string _verdict = CitationSupportChecker.Supported;
    private readonly FakeChatService _model;

    public ReportEditTests() => _model = new FakeChatService().RespondsWith(Respond);

    public void Dispose() => TestFolders.TryDelete(_root);

    /// <summary>The run's own answers, and for an edited sentence the verdict the test asks for, quoting the abstract.</summary>
    private string Respond(string prompt)
    {
        if (!prompt.Contains(CitationSupportChecker.EditedSentencesNotice)) return ReviewWriterTests.Respond(prompt);
        string abstractText = Regex.Match(prompt, @"E1: ([^\n]*)").Groups[1].Value.Trim();
        return _verdict == CitationSupportChecker.Supported
            ? JsonSerializer.Serialize(new { results = new[] { new { sentence = 1, verdict = "supported", excerpt = "E1", quote = abstractText, reason = "The abstract says so." } } })
            : """{"results":[{"sentence":1,"verdict":"not_supported","excerpt":"E1","quote":"","reason":"The paper says something else."}]}""";
    }

    private PrismaReviewEngine Engine() =>
        new("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => _model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };

    private async Task<(PrismaReviewEngine Engine, Guid RunId, string Key)> OwnedRun()
    {
        var engine = Engine();
        var runId = Guid.NewGuid();
        string key = engine.ClaimRun(runId);
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));
        return (engine, runId, key);
    }

    private static string MainTex(PrismaReviewEngine engine, Guid runId)
    {
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        return reader.ReadToEnd();
    }

    private static bool InArchive(PrismaReviewEngine engine, Guid runId, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        return archive.GetEntry(name) != null;
    }

    private const string NewSentence = "Supervisor checks let agent loops recover from faults [1].";

    [Fact]
    public async Task AnEditIsCheckedAgainOnTheFastCheckAndKeptWithItsQuote()
    {
        var (engine, runId, key) = await OwnedRun();
        string before = engine.LoadReportEdits(runId).Edits.Count == 0 ? "none" : "some";
        int calls = _model.Prompts.Count;

        var edit = await engine.EditReportSentenceAsync(runId, "discussionItem", 0, "  " + NewSentence + " ", editKey: key);

        Assert.Equal("none", before);
        Assert.Equal(1, _model.Prompts.Count - calls); // one cheap call for the one cited study, no second check
        Assert.Contains(PromptSafety.OpenTag + " (sentence written by the reviewer)\n" + NewSentence, _model.Prompts[^1]);
        Assert.Equal(NewSentence, edit.After);
        Assert.Equal("Recovery and audits complement each other [1, 2].", edit.Before);
        Assert.Equal("run owner (edit key)", edit.Editor);
        var check = Assert.Single(edit.Checks);
        Assert.Equal((CitationSupportChecker.Supported, 1, true), (check.Verdict, check.Reference, check.QuoteVerified));
        Assert.False(string.IsNullOrWhiteSpace(check.Quote));

        var onFile = engine.LoadReportEdits(runId);
        Assert.Equal(NewSentence, Assert.Single(onFile.Edits).After);
        Assert.True(InArchive(engine, runId, ReportEditLog.FileName));
    }

    [Fact]
    public async Task AnUnsupportedCitationIsKeptAndMarked()
    {
        var (engine, runId, key) = await OwnedRun();
        _verdict = CitationSupportChecker.NotSupported;

        var edit = await engine.EditReportSentenceAsync(runId, "discussionItem", 0, NewSentence, editKey: key);

        Assert.Equal(CitationSupportChecker.NotSupported, Assert.Single(edit.Checks).Verdict);
        string tex = MainTex(engine, runId);
        Assert.Contains("Supervisor checks let agent loops recover from faults", tex);
        Assert.Contains("0 supported, 0 partly supported and 1 not supported", tex);
        Assert.Contains("kept and marked", tex);
    }

    [Fact]
    public async Task CodeWrittenPartsBadSentencesAndOtherBrowsersAreRefused()
    {
        var (engine, runId, key) = await OwnedRun();
        async Task<string> Refused(string field, int index, string text, string? withKey) =>
            (await Assert.ThrowsAsync<PrismaReviewEngine.ReportEditException>(() => engine.EditReportSentenceAsync(runId, field, index, text, editKey: withKey))).Message;

        Assert.Contains("Only the browser that started this run", await Refused("discussionItem", 0, NewSentence, null));
        Assert.Contains("Only the browser that started this run", await Refused("discussionItem", 0, NewSentence, key + "x"));
        foreach (var field in new[] { "eligibilityItem", "searchStrategyItem", "selectionProcessItem", "synthesisMethodsItem", "citationCheckSummary", "reviewLimitationsItem", "abstractItem" })
            Assert.Contains("written in code", await Refused(field, 0, NewSentence, key));
        Assert.Contains("not in the reference list", await Refused("discussionItem", 0, "Supervisor checks matter [9].", key));
        Assert.Contains("Write one sentence", await Refused("discussionItem", 0, "First claim [1]. Second claim [2].", key));
        Assert.Contains("not in this part", await Refused("discussionItem", 5, NewSentence, key));
        Assert.Contains("has not changed", await Refused("discussionItem", 0, "Recovery and audits complement each other [1, 2].", key));
        Assert.Empty(engine.LoadReportEdits(runId).Edits);
        await Assert.ThrowsAsync<PrismaReviewEngine.ReportEditException>(() => engine.UndoReportEditAsync(runId, "discussionItem", 0, editKey: key));
    }

    [Fact]
    public async Task MainTexIsBuiltFromTheEditedTextAndUndoRestoresIt()
    {
        var (engine, runId, key) = await OwnedRun();
        string original = MainTex(engine, runId);
        // main.tex cites with \cite and the keys of references.bib (issue #537).
        Assert.Matches(@"Recovery and audits complement each other~\\cite\{[^,}]+,[^,}]+\}\.", original);

        await engine.EditReportSentenceAsync(runId, "discussionItem", 0, NewSentence, editKey: key);
        string edited = MainTex(engine, runId);
        Assert.Matches(@"Supervisor checks let agent loops recover from faults~\\cite\{[^,}]+\}\.", edited);
        Assert.DoesNotContain("Recovery and audits complement each other", edited);
        Assert.Contains("After the run, the reviewer edited 1 sentence of the generated text.", edited);
        Assert.Contains("1 supported, 0 partly supported and 0 not supported", edited);

        int calls = _model.Prompts.Count;
        var undo = await engine.UndoReportEditAsync(runId, "discussionItem", 0, editKey: key);
        Assert.Equal(calls, _model.Prompts.Count); // no model call
        Assert.Equal((ReportEdit.Undone, 1, "Recovery and audits complement each other [1, 2]."), (undo.Action, undo.Undoes, undo.After));
        Assert.Equal(original, MainTex(engine, runId));
        Assert.Equal(2, engine.LoadReportEdits(runId).Edits.Count); // the history keeps both
    }

    [Fact]
    public async Task TheStatementOnAiUseCountsTheEditsOnlyWhenThereAreSome()
    {
        var (engine, runId, key) = await OwnedRun();
        var state = engine.LoadState(runId)!;
        string folder = engine.Store.FolderOf(runId);
        string plain = AiUseStatement.Methods(state, runId);
        Assert.Equal(plain, AiUseStatement.Methods(state, runId, ReportEditLog.Load(folder)));

        await engine.EditReportSentenceAsync(runId, "synthesis-1", 0, NewSentence, editKey: key);
        string withEdits = AiUseStatement.Methods(state, runId, ReportEditLog.Load(folder));
        Assert.Contains("the reviewer edited 1 sentence of the generated text", withEdits);
        Assert.Contains("checked again against the source text kept from the run", withEdits);
        Assert.EndsWith("take full responsibility for the content.", withEdits);
    }
}

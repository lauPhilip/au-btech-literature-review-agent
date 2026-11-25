using System.Runtime.CompilerServices;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Extraction directed by the research questions (issue #430): the questions' words choose the passages, every key
/// finding names its question (checked in code), extra findings are kept only for a question not yet covered, an
/// identical extraction is reused from the cache, and a run without questions sends the same prompt as before
/// (Golden/extraction-prompt-no-questions.txt, written by the code before this change; UPDATE_GOLDEN=1 rewrites it).
/// </summary>
public class QuestionDirectedExtractionTests : IDisposable
{
    private const string Fault = "The results show that fault injection measured fault coverage of the agent runtime.";
    // Without "measured", which the appraisal passages look for, so only the extraction passages can bring it in.
    private const string Raised = "The results show that fault injection raised fault coverage of the agent runtime.";
    private const string Audit = "Audit logging recorded every action of the agents.";
    private const string Oversight = "Human oversight stopped unsafe actions before they ran.";
    private const string Gains = "Students reported improvements of 40-90% over conventional teaching.";
    private static readonly string[] Questions = { "How is fault injection used to measure fault coverage?", "Which controls keep agents safe?" };

    private readonly string _root = Path.Join(Path.GetTempPath(), "extract-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private static ReferencedPaper Paper(params string[] passages) =>
        new(1, "p1", "Safe agents in production", "", passages.Select((t, i) => new DocumentChunk("p1", i + 1, t)).ToList());

    /// <summary>Twenty passages the generic extraction words rank first, then the one that answers RQ1.</summary>
    private static ReferencedPaper FillerThenFault(string fault = Fault) =>
        Paper(Enumerable.Range(1, 20).Select(i => $"The results and findings show the method evaluation of case {i}.").Append(fault).Append(Audit).Append(Oversight).ToArray());

    private static string Finding(string quote, string? question) =>
        $$"""{"value":"a finding","quote":"{{quote}}"{{(question == null ? "" : $",\"question\":\"{question}\"")}}}""";

    private static string Answer(params string[] findings) =>
        $$"""{"studyType":"experiment","method":{"value":"not reported","quote":""},"sample":{"value":"not reported","quote":""},"keyFindings":[{{string.Join(",", findings)}}],"limitations":{"value":"not reported","quote":""},"appraisalCategory":"not_empirical","appraisal":[]}""";

    [Fact]
    public async Task TheQuestionsWordsChooseThePassages()
    {
        var without = new FakeChatService().RespondsWith(_ => Answer(Finding(Raised, null)));
        await StudyExtractor.ExtractAsync(without, FillerThenFault(Raised));
        var with = new FakeChatService().RespondsWith(_ => Answer(Finding(Raised, "RQ1")));
        await StudyExtractor.ExtractAsync(with, FillerThenFault(Raised), focus: new ExtractionFocus(Questions, "Safe agents"));

        Assert.DoesNotContain(Raised, without.Prompts[0]);
        Assert.Contains(Raised, with.Prompts[0]);
        Assert.Contains("RQ1: How is fault injection used to measure fault coverage?", with.Prompts[0]);
        Assert.Contains("REVIEW OBJECTIVE: \"Safe agents\"", with.Prompts[0]);
        Assert.Contains("\"question\":\"RQ1\"", with.Prompts[0]);
    }

    [Fact]
    public async Task AFindingWithAnUnknownQuestionIsSentBack()
    {
        var chat = new FakeChatService().Returns(Answer(Finding(Fault, "RQ7"))).Returns(Answer(Finding(Fault, "rq1")));
        var e = await StudyExtractor.ExtractAsync(chat, Paper(Fault), focus: new ExtractionFocus(Questions));

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("keyFindings[0].question must be one of RQ1, RQ2 or none", chat.Prompts[1]);
        var f = Assert.Single(e.KeyFindings);
        Assert.Equal("RQ1", f.Question);
        Assert.True(f.QuoteVerified);
    }

    [Fact]
    public async Task ExtraFindingsAreKeptOnlyForAQuestionNotYetCovered()
    {
        var chat = new FakeChatService().Returns(Answer(
            Finding(Fault, "RQ1"), Finding(Gains, "none"), Finding(Fault, "RQ1"), Finding(Fault, "RQ1"), Finding(Audit, "RQ2"), Finding(Oversight, "RQ2")));
        var e = await StudyExtractor.ExtractAsync(chat, Paper(Fault, Audit, Oversight, Gains), focus: new ExtractionFocus(Questions));

        Assert.Equal(new[] { "RQ1", "none", "RQ1", "RQ2" }, e.KeyFindings.Select(f => f.Question));
        Assert.Equal(Audit, e.KeyFindings[3].Quote);
        Assert.All(e.KeyFindings, f => Assert.True(f.QuoteVerified));
    }

    [Fact]
    public async Task WithoutQuestionsFindingsNameNoQuestionAndStayAtThree()
    {
        var chat = new FakeChatService().Returns(Answer(Finding(Fault, "RQ1"), Finding(Audit, null), Finding(Oversight, null), Finding(Gains, null)));
        var e = await StudyExtractor.ExtractAsync(chat, Paper(Fault, Audit, Oversight, Gains));

        Assert.Equal(3, e.KeyFindings.Count);
        Assert.All(e.KeyFindings, f => Assert.Null(f.Question));
    }

    [Fact]
    public async Task AnIdenticalExtractionIsReusedFromTheCache()
    {
        var cache = new ReviewCache(new CacheOptions { Folder = _root }, _root);
        var focus = new ExtractionFocus(Questions, "Safe agents", cache, "model-a");
        var chat = new FakeChatService().RespondsWith(_ => Answer(Finding(Fault, "RQ1")));

        var first = await StudyExtractor.ExtractAsync(chat, Paper(Fault, Audit), focus: focus);
        var second = await StudyExtractor.ExtractAsync(chat, Paper(Fault, Audit), focus: focus);
        Assert.Single(chat.Prompts);
        Assert.Null(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal("RQ1", Assert.Single(second.KeyFindings).Question);
        Assert.True(second.KeyFindings[0].QuoteVerified);

        // Another model, other questions or other text is a new extraction.
        await StudyExtractor.ExtractAsync(chat, Paper(Fault, Audit), focus: focus with { Model = "model-b" });
        await StudyExtractor.ExtractAsync(chat, Paper(Fault, Audit), focus: focus with { Questions = new[] { Questions[0] } });
        await StudyExtractor.ExtractAsync(chat, Paper(Fault, Oversight), focus: focus);
        Assert.Equal(4, chat.Prompts.Count);
    }

    [Fact]
    public void TheMethodsSayHowTheQuestionsDirectedTheExtractionAndWhatWasReused()
    {
        var plain = new StudyExtraction { ReferenceNumber = 1, KeyFindings = { new EvidencedValue { Value = "v", Quote = "q" } } };
        string before = MethodsSectionWriter.DataAndAppraisal(new[] { plain });
        Assert.DoesNotContain("research question", before);
        Assert.DoesNotContain("reused", before);

        var directed = new StudyExtraction { ReferenceNumber = 1, FromCache = true, KeyFindings = { new EvidencedValue { Value = "v", Quote = "q", Question = "RQ1" } } };
        string after = MethodsSectionWriter.DataAndAppraisal(new[] { directed });
        Assert.Contains("each key finding names the research question it bears on", after);
        Assert.Contains("1 extraction was reused from an earlier identical one", after);
    }

    private static string GoldenPath([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", "extraction-prompt-no-questions.txt");

    [Fact]
    public async Task ARunWithoutQuestionsSendsTheSamePromptAsBefore()
    {
        var chat = new FakeChatService().RespondsWith(_ => Answer(Finding(Fault, null)));
        await StudyExtractor.ExtractAsync(chat, FillerThenFault());
        string prompt = chat.Prompts[0].Replace("\r\n", "\n");

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, prompt);
        }
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), prompt);
    }
}

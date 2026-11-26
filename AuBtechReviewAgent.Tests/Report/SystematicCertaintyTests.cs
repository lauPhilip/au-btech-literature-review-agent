using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The systematic review's certainty ratings: built from the extraction, the MMAT answers and the codebook, and shown
/// in main.tex (method, table, phrase after each answer) and certainty.json. A run without research questions has no
/// answers to rate, so its main.tex is unchanged (Golden/systematic-main.tex).
/// </summary>
public class SystematicCertaintyTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "certainty-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static StudyExtraction Study(int r, string basis, string category, params string[] answers) => new()
    {
        ReferenceNumber = r, EvidenceBasis = basis, AppraisalCategory = category,
        Appraisal = answers.Select((a, i) => new AppraisalAnswer { Id = $"1.{i + 1}", Answer = a }).ToList(),
    };

    [Fact]
    public void TheStudiesAreReadFromTheExtractionAndCantTellIsNotAFailure()
    {
        var book = new ThematicCodebook
        {
            Codes = { new ThematicCode { Reference = 3, Questions = new() { "RQ2" } } },
            Themes = { new Theme { Id = "T1", Name = "Recovery", Studies = { 2, 3 }, Questions = new() { "RQ1" } } },
            Unanswered = new() { new UnansweredQuestion("RQ2", "No study certifies agent loops.") },
        };
        var extractions = new List<StudyExtraction>
        {
            Study(1, "full text", "qualitative", "cant_tell", "cant_tell", "cant_tell", "no", "no"),
            Study(2, "abstract only", "quantitative_descriptive", "no", "no", "no", "yes", "cant_tell"),
            Study(3, "full text", "not_empirical"),
        };
        var report = new PrismaReport
        {
            ResearchAnswers =
            {
                new ResearchAnswer { Number = 1, Question = "How do agent loops recover?", Answer = "They recover through supervisor checks [1]." },
                new ResearchAnswer { Number = 2, Question = "How are they certified?", Answer = ReviewWriter.UnansweredText(WritingProfile.Systematic) },
            },
        };

        var file = SystematicCertainty.Assess(report, book, extractions)!;

        var sources = SystematicCertainty.Sources(book, extractions).ToDictionary(s => s.Reference);
        Assert.False(sources[1].FailsAppraisal); // two of five are "no"; three "can't tell" are not failures
        Assert.True(sources[2].FailsAppraisal);
        Assert.True(sources[2].AbstractOnly);
        Assert.False(sources[3].Empirical);
        Assert.Null(sources[1].Questions);
        Assert.Equal(new[] { "RQ2" }, sources[3].Questions);

        var rq1 = file.Answers[0];
        Assert.Equal(new[] { 1, 2, 3 }, rq1.Sources); // cited [1], and theme T1's [2] and [3]
        Assert.Equal("moderate", rq1.Start);
        Assert.Equal("moderate", rq1.Overall); // nothing is more than half: one of three fails, one is abstract only, one has no code on RQ1
        var rq2 = file.Answers[1];
        Assert.Null(rq2.Overall);
        Assert.Equal("No evidence bears on this question (No study certifies agent loops), so it was answered in code and there is nothing to rate.", rq2.NotRated);
        Assert.Equal(EvidenceCertainty.Method(), file.Method);
        Assert.Null(SystematicCertainty.Assess(new PrismaReport(), book, extractions));
    }

    [Fact]
    public void CoherenceReadsTheSecondCodingsDisagreementsOnTheQuestionsThemes()
    {
        var book = new ThematicCodebook
        {
            SecondCoding = new SecondCoding
            {
                CellsCompared = 6, Agreements = 3,
                Disagreements = { "Study 2, T1 (Recovery): first coding only", "Study 4, T2 (Oversight): second coding only", "Study 5, T1 (Recovery): second coding only" },
            },
        };

        Assert.Equal(new[] { 2, 5 }, SystematicCertainty.Contested(book, new[] { "t1" }));
        Assert.Empty(SystematicCertainty.Contested(book, new[] { "T3" })!);
        Assert.Null(SystematicCertainty.Contested(new ThematicCodebook(), new[] { "T1" }));
        Assert.Null(SystematicCertainty.Contested(new ThematicCodebook { SecondCoding = new SecondCoding { Error = "failed", CellsCompared = 4 } }, new[] { "T1" }));
    }

    [Fact]
    public async Task MainTexGainsTheMethodTheTableAndThePhraseAndTheArchiveHoldsCertaintyJson()
    {
        var model = new FakeChatService().RespondsWith(ResearchQuestionWritingTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, ResearchQuestions: ResearchQuestionWritingTests.Questions));

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        string Entry(string name) { using var r = new StreamReader(archive.GetEntry(name)!.Open()); return r.ReadToEnd().Replace("\r\n", "\n"); }
        string tex = Entry("main.tex");
        var file = JsonSerializer.Deserialize<CertaintyFile>(Entry(CertaintyFile.FileName))!;

        Assert.Equal(2, file.Answers.Count);
        Assert.NotNull(file.Answers[0].Overall);
        Assert.NotNull(file.Answers[1].NotRated);
        Assert.Equal(0.5, file.Rule.ConcernShare);
        Assert.Contains("\\subsection{Certainty of the Evidence}\\label{sec:certainty-of-the-evidence}\n" + PaperLatex.Escape(EvidenceCertainty.Method()), tex);
        Assert.Contains(": Certainty of the Evidence for Each Answer}", tex);
        Assert.Contains(PaperLatex.Escape(file.Answers[0].Phrase) + "\\par", tex);
        Assert.Contains("(certainty not rated: no evidence bears on this question)\\par", tex);
        // Every answer is rated the same way again from the same files.
        using var again = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(again.GetEntry(CertaintyFile.FileName)!.Open());
        Assert.Equal(Entry(CertaintyFile.FileName), reader.ReadToEnd().Replace("\r\n", "\n"));
    }
}

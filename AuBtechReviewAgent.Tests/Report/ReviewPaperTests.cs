using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The paper of a review as one shared shape (module design M9). A whole systematic review runs offline with the
/// fake model of <see cref="ReviewWriterTests"/>, and its main.tex is compared, line for line, with the main.tex the
/// systematic review wrote before the shape was shared (Golden/systematic-main.tex). Dates, times and the protocol
/// hash change from run to run, so they are blanked in both. Set UPDATE_GOLDEN=1 to write the file again after a
/// deliberate change.
/// </summary>
public class ReviewPaperTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "paper-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string GoldenPath([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", "systematic-main.tex");

    /// <summary>The main.tex of one offline systematic review, with what changes between runs blanked.</summary>
    internal static async Task<string> SystematicMainTexAsync(string root)
    {
        var model = new FakeChatService().RespondsWith(ReviewWriterTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string tex = reader.ReadToEnd().Replace("\r\n", "\n");
        tex = Regex.Replace(tex, @"\d{4}-\d{2}-\d{2}( \d{2}:\d{2}(:\d{2})?)?", "DATE");
        tex = Regex.Replace(tex, @"Protocol fingerprint: \S+", "Protocol fingerprint: HASH");
        tex = Regex.Replace(tex, @"% Run: [^;]+; tool: .*", "% Run: RUN; tool: TOOL.");
        tex = Regex.Replace(tex, @"fingerprint (?:[0-9a-f]|\\allowbreak\{\}){16,}", "fingerprint FINGERPRINT"); // with its break points
        tex = Regex.Replace(tex, @"version [^ ]+\)", "version VERSION)");
        tex = Regex.Replace(tex, @"This run used version .*?(?=\s+The run archive)", "This run used a version of the tool.");
        // The statement on AI use names the day and the run (the publisher audit, batch A).
        tex = Regex.Replace(tex, @"\b\d{1,2} (?:January|February|March|April|May|June|July|August|September|October|November|December) \d{4}\b", "DAY");
        tex = Regex.Replace(tex, @"\(run [^)]*\)", "(run RUNID)");
        return tex;
    }

    [Fact]
    public async Task TheSystematicReviewWritesTheSameMainTexAsBefore()
    {
        string tex = await SystematicMainTexAsync(_root);

        Assert.Contains(@"\section{Results \& Synthesis}", tex);
        Assert.Contains(@"\subsection{Recovery and oversight}", tex);
        Assert.Contains("PRISMA 2020 flow diagram", tex);
        // Issue #133: Table 3.1 names the database as the paper page does.
        Assert.Contains(@"& \textbf{Source} & \textbf{Citation (APA 7th)}", tex);
        Assert.Contains("& Google Scholar &", tex);

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tex);
        }
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), tex);
    }

    [Fact]
    public void AnotherKindOfReviewFillsTheSameShapeWithItsOwnParts()
    {
        var paper = new ReviewPaper
        {
            KindLabel = "AI-GENERATED GREY LITERATURE REVIEW",
            Title = "Context engineering for LLM agents",
            ProtocolHash = "9c00331882d1",
            GeneratedAt = "2026-10-08 12:00:00 UTC",
            Abstract = "We reviewed 25 sources.",
            Rationale = "Agents need context [1].",
            Objectives = "To map the practices.",
            Methods = { new("Planning (G1–G5)", "The protocol was written before any search."), new("Quality (G11)", "", @"\textbf{Table 7} checklist") },
            DataHeading = "Data",
            DataText = "123 sources were found.",
            DataLatex = "\\begin{figure}[H]FLOW\\end{figure}\n",
            ResultsHeading = "Results by research question",
            ResultsText = "Five questions.",
            Themes = { new("RQ1: Retrieval & memory", "Sources fetch only what is needed [1, 2].") },
            ResultsLatex = { "MAP\n" },
            Discussion = "State of the art against the state of the practice [2].",
            Declarations = { new("Use of AI", "A language model wrote the sections; code checked every citation.") },
            References = { "Ana (2025). *Keeping context short*. blog.example.org", "Bo (2026). Retrieval first_v2." },
        };

        string tex = PaperLatex.Build(paper);

        Assert.Contains("AI-GENERATED GREY LITERATURE REVIEW", tex);
        Assert.Contains(@"\subsection{Planning (G1–G5)}", tex);
        Assert.Contains("\\subsection{Quality (G11)}\\label{sec:quality-g11}\n\n\\textbf{Table 7} checklist".Replace("\n", Environment.NewLine), tex); // LaTeX built in code is kept as it is
        Assert.Contains(@"\section{Data}", tex);
        Assert.Contains(@"\subsection{RQ1: Retrieval \& memory}", tex); // plain text is escaped
        Assert.Contains("\\end{multicols}\n\nMAP\n\n\\begin{multicols*}{2}".Replace("\n", Environment.NewLine), tex); // a wide block leaves the columns
        Assert.Contains(@"\bibitem{ref1} Ana (2025). \textit{Keeping context short}. blog.example.org", tex); // no keys given: ref1, ref2, ...
        Assert.Contains(@"\bibitem{ref2} Bo (2026). Retrieval first\_v2", tex);
        Assert.DoesNotContain("PRISMA", tex);
    }

    [Fact]
    public void AnAppendixComesAfterTheReferencesOnlyWhenThereIsOne()
    {
        // Issue #139: the tables of the included studies go to an appendix after the references.
        var paper = new ReviewPaper { Title = "T", References = { "Ana (2025). Keeping context short." }, AppendixLatex = "APPENDIX TABLES\n" };
        string tex = PaperLatex.Build(paper).Replace("\r\n", "\n");

        int references = tex.IndexOf(@"\end{thebibliography}", StringComparison.Ordinal);
        int appendix = tex.IndexOf(@"\section*{Appendix}", StringComparison.Ordinal);
        Assert.True(references > 0 && appendix > references, "the appendix comes after the references");
        Assert.True(tex.IndexOf("APPENDIX TABLES", StringComparison.Ordinal) > appendix);
        Assert.EndsWith("APPENDIX TABLES\n\n\\end{document}\n", tex); // the appendix's LaTeX is kept as it is

        string without = PaperLatex.Build(new ReviewPaper { Title = "T" });
        Assert.DoesNotContain("Appendix", without);
    }
}

using System.Globalization;
using System.IO.Compression;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The systematic review's own parts of main.tex that the golden run does not reach: the artifact as a table or a
/// list, the answers to the research questions, the TikZ block of the synthesis, the year axis and the pie charts.
/// </summary>
public class ArchiveLatexTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "archive-latex-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private string MainTex(PrismaReport report, List<IncludedPaperMetricRow> records)
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 }) { WorkspaceRoot = _root };
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchive(Guid.NewGuid(), report, records)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static List<IncludedPaperMetricRow> TwoJournalPapers() => new()
    {
        new() { Title = "Undated", Year = 0, VenueType = "Journals", ReferenceNumber = 2, ApaCitation = "Bo (n.d.). Undated." },
        new() { Title = "Loops", Year = 2025, VenueType = "Journals", ReferenceNumber = 1, ApaCitation = "Ana (2025). Loops." },
    };

    [Fact]
    public void ATableArtifactAnswersTikzAndTheYearAxisAreWrittenInCode()
    {
        var report = new PrismaReport
        {
            TitleItem = "Agent loops",
            SynthesisResultsItem = "Overview.[TIKZ_START]\\draw (a) -¿ (b);[TIKZ_END]",
            ConclusionItem = "Loops recover.",
            ResearchAnswers = { new ResearchAnswer { Number = 1, Question = "How do loops recover?", Answer = "Through checks." } },
            Artifact = new ReviewArtifact
            {
                Kind = ArtifactKinds.Table, Title = "Design & Rules", Caption = "From the studies.",
                Columns = { "Rule", "Source" }, Rows = { new List<string> { "Check", "Supervisor" } },
            },
        };

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("da-DK");
        string tex;
        try
        {
            tex = MainTex(report, TwoJournalPapers());
            Assert.Equal("da-DK", CultureInfo.CurrentCulture.Name); // the culture is given back
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // Undated papers are plotted last as "n.d.", and decimals are written with a point whatever the culture.
        Assert.Contains("xtick={1,2}, xticklabels={2025,n.d.}, xmin=0.5, xmax=2.5, ymin=0, ymax=4,", tex);
        Assert.Contains(@"\addplot[fill=blue!50] coordinates {(1,1) (2,1)};", tex);
        Assert.Contains(@"\draw[fill=blue!50] (0,0) circle (1.2cm); \node at (0,0) {\scriptsize 2};", tex);
        Assert.Contains("\\resizebox{\\linewidth}{!}{\n\\draw (a) -> (b);\n}\n\\caption{Grounded Architectural Synthesis System Model Diagram}", tex);
        Assert.Contains("\\begin{tabularx}{\\textwidth}{X X}\n\\multicolumn{2}{l}{\\textbf{Table 4.2: Design \\& Rules}} \\\\\n\\toprule\n\\textbf{Rule} & \\textbf{Source} \\\\", tex);
        Assert.Contains("Check & Supervisor \\\\ \\hline\n\\end{tabularx}\n\\vspace{2pt}\\noindent\\small From the studies.\\normalsize\n\\vspace{10pt}", tex);
        // An answer that cites no study is not rated, and the certainty summary closes the answers (issue #551).
        Assert.Contains("\\noindent\\textbf{RQ1. How do loops recover?} Through checks. (certainty not rated: no evidence bears on this question)\\par\\smallskip\n" +
                        "\\noindent The answer to the research question is not rated, because no evidence bears on it.\\par\\smallskip\nLoops recover.", tex);
        // Table A.1 and the references follow the reference numbers.
        Assert.True(tex.IndexOf("Ana (2025)", StringComparison.Ordinal) < tex.IndexOf("Bo (n.d.)", StringComparison.Ordinal));
        Assert.Contains(PrismaReviewEngine.NoProtocolText, tex);
    }

    [Fact]
    public void AListArtifactIsABulletListAndAFailedOneIsLeftOut()
    {
        var bullets = new PrismaReport
        {
            Artifact = new ReviewArtifact { Kind = ArtifactKinds.Bullets, Caption = "Key points", Bullets = { "Check often", "Log 50%" } },
        };
        string tex = MainTex(bullets, TwoJournalPapers());
        Assert.Contains("\\noindent\\textbf{Artifact}\n\\par\\noindent Key points\n\\begin{itemize}\n\\item Check often\n\\item Log 50\\%\n\\end{itemize}", tex);

        var failed = new PrismaReport { Artifact = new ReviewArtifact { Kind = ArtifactKinds.Bullets, Bullets = { "Invented" }, Error = "no model" } };
        string without = MainTex(failed, TwoJournalPapers());
        Assert.DoesNotContain("Invented", without);
        Assert.DoesNotContain(@"\begin{itemize}", without);
    }

    [Fact]
    public void APieHasOneSlicePerCountedLabelAndALegendForEveryLabel()
    {
        string nl = Environment.NewLine;
        Assert.Equal(@"\draw[fill=gray!20] (0,0) circle (1.2cm); \node at (0,0) {No Data};" + nl,
            PrismaReviewEngine.LatexPie(new List<(string, int, string)> { ("A", 0, "red") }));

        string pie = PrismaReviewEngine.LatexPie(new List<(string, int, string)> { ("A", 1, "c1"), ("B", 0, "c2"), ("C_x & y", 3, "c3") });
        Assert.Equal(string.Join(nl, new[]
        {
            @"\draw[fill=c1] (0,0) -- (0:1.2cm) arc (0:90:1.2cm) -- cycle; \node at (45:0.8cm) {\scriptsize 1};",
            @"\draw[fill=c3] (0,0) -- (90:1.2cm) arc (90:360:1.2cm) -- cycle; \node at (225:0.8cm) {\scriptsize 3};",
            @"\matrix [draw,below,matrix of nodes,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {",
            @"\node[fill=c1,label=right:(1) A] {}; & \node[fill=c2,label=right:(0) B] {}; \\",
            @"\node[fill=c3,label=right:(3) C\_x \& y] {}; & \\",
            @"};",
        }) + nl, pie);
    }
}

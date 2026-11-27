using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #538: a diagram the model wrote in TikZ is checked in code before main.tex. Boxes and arrows pass and are
/// drawn again in code; a command that reads or writes a file, defines a macro or changes how TeX reads characters,
/// and anything that is not a box or an arrow, rejects the whole diagram, which main.tex then leaves out.
/// </summary>
public class TikzDiagramTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "tikz-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Valid = """
        \begin{tikzpicture}[node distance=2cm, every node/.style={draw}]
        % the loop
        \node[draw, rounded corners] (plan) {Plan \\ the step};
        \node[right=2cm of plan] (act) {Act on 50\% & log_it [1]};
        \node (check) at (1.5,-2) {Check};
        \draw[->, thick] (plan) -- node[above] {then} (act) -- (check);
        \draw[-{Stealth[length=2mm]}, dashed] (check) -| (plan);
        \path (act) edge[<->] (check);
        \end{tikzpicture}
        """;

    [Fact]
    public void BoxesAndArrowsPassAndAreDrawnAgainInCode()
    {
        var (diagram, problem) = TikzDiagram.Check(Valid);
        Assert.Null(problem);
        Assert.Equal(new[] { "plan", "act", "check" }, diagram!.Nodes.Select(n => n.Id));
        Assert.Equal(4, diagram.Edges.Count);

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("da-DK");
        string tex;
        try { tex = diagram.ToLatex().Replace("\r\n", "\n"); }
        finally { CultureInfo.CurrentCulture = previous; }

        Assert.StartsWith(@"\begin{tikzpicture}[node distance=0.8cm and 1.2cm, font=\scriptsize,", tex);
        Assert.Contains(@"\node[box] (plan) {Plan\\ the step};", tex);
        Assert.Contains(@"\node[box, right=of plan] (act) {Act on 50\% \& log\_it~\cite{ref1}};", tex); // escaped, the distance dropped
        Assert.Contains(@"\node[box] (check) at (1.5,-2) {Check};", tex); // a point, whatever the culture
        Assert.Contains(@"\draw[link, ->] (plan) -- node[midway, fill=white, font=\tiny, align=center] {then} (act);", tex);
        Assert.Contains(@"\draw[link, ->] (act) -- (check);", tex);
        Assert.Contains(@"\draw[link, ->, dashed] (check) -| (plan);", tex);
        Assert.Contains(@"\draw[link, <->] (act) -- (check);", tex);
        Assert.DoesNotContain("rounded corners", tex); // the model's styles are not kept
        Assert.DoesNotContain("% the loop", tex);
    }

    [Theory]
    [InlineData(@"\node (a) {A}; \input{/etc/passwd}", "the command \\input")]
    [InlineData(@"\node (a) {\input{/etc/passwd}};", "the command \\input")]
    [InlineData(@"\node (a) {A}; \immediate\write18{rm -rf /}", "the command \\immediate")]
    [InlineData(@"\node (a) {A}; \write18{curl evil}", "the command \\write")]
    [InlineData(@"\newwrite\f \openout\f=x.tex \node (a) {A};", "the command \\newwrite")]
    [InlineData(@"\openin1=/etc/passwd \read1 to \x \node (a) {\x};", "the command \\openin")]
    [InlineData(@"\catcode`\@=11 \node (a) {A};", "the command \\catcode")]
    [InlineData(@"\def\x{\input} \node (a) {A};", "the command \\def")]
    [InlineData(@"\let\x\relax \node (a) {A};", "the command \\let")]
    [InlineData(@"\node (a) {\csname input\endcsname{x}};", "the command \\csname")]
    [InlineData(@"\node (a) {^^5cinput{/etc/passwd}};", "it holds a ^^ character code")]
    [InlineData(@"\include{secret} \node (a) {A};", "the command \\include")]
    public void ACommandThatReadsWritesOrRedefinesRejectsTheDiagram(string code, string problem)
    {
        var (diagram, why) = TikzDiagram.Check(code);
        Assert.Null(diagram);
        Assert.Equal(problem.StartsWith("it holds", StringComparison.Ordinal) ? problem : "it holds " + problem, why);
    }

    [Theory]
    [InlineData("", "the block is empty")]
    [InlineData(@"\draw (a) -- (b);", "it has no boxes")]
    [InlineData(@"\node (a) {A}; \draw[->] (a) -- (b);", "an arrow joins a box that is not drawn")]
    [InlineData(@"\node (a) {\textbf{A}};", "a statement is neither a box nor an arrow")]
    [InlineData(@"\node (a) {A \relax};", "a label holds a command")]
    [InlineData(@"\node (a) {A}; \foreach \i in {1,2} {\node (b\i) {B};}", "it holds the command \\foreach")]
    [InlineData(@"\node (a) {A}; \fill (0,0) circle (1cm);", "a statement is neither a box nor an arrow")]
    [InlineData(@"\node (a) {A}; \node (a) {B};", "two boxes have the same name")]
    [InlineData(@"\node[right=of b] (a) {A}; \node (b) {B};", "a box is placed next to a box drawn after it")]
    [InlineData(@"\node (a) {A}", "a statement does not end with \";\"")]
    [InlineData(@"\node (a) {A};}", "its braces do not balance")]
    [InlineData(@"\node (a) {A}; \draw (a) -- (a) -- ;", "an arrow is not drawn from box to box")]
    public void AnythingButBoxesAndArrowsRejectsTheDiagram(string code, string problem)
    {
        var (diagram, why) = TikzDiagram.Check(code);
        Assert.Null(diagram);
        Assert.Equal(problem, why);
    }

    [Fact]
    public void TheDecisionIsOnFileWhenTheModelWroteABlock()
    {
        Assert.Equal("", TikzDiagram.Checked("no block here", _root));
        Assert.False(File.Exists(Path.Join(_root, TikzDiagram.FileName)), "no block, no decision to record");

        string good = "\n\n[TIKZ_START]\n\\node (a) {A};\n[TIKZ_END]\n";
        Assert.Equal(good, TikzDiagram.Checked(good, _root));
        using (var accepted = JsonDocument.Parse(File.ReadAllText(Path.Join(_root, TikzDiagram.FileName))))
        {
            Assert.True(accepted.RootElement.GetProperty("Accepted").GetBoolean());
            Assert.Equal(1, accepted.RootElement.GetProperty("Boxes").GetInt32());
        }

        Assert.Equal("", TikzDiagram.Checked("[TIKZ_START]\\node (a) {A}; \\input{/etc/passwd}[TIKZ_END]", _root));
        using var rejected = JsonDocument.Parse(File.ReadAllText(Path.Join(_root, TikzDiagram.FileName)));
        Assert.False(rejected.RootElement.GetProperty("Accepted").GetBoolean());
        Assert.Equal("it holds the command \\input", rejected.RootElement.GetProperty("Problem").GetString());
    }

    [Fact]
    public void TheArchiveListsTheDecisionFile() =>
        Assert.Contains(TikzDiagram.FileName, ReviewModules.Find("systematic")!.ArchiveFiles);

    private string MainTex(PrismaReport report)
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 }) { WorkspaceRoot = _root };
        var records = new List<IncludedPaperMetricRow> { new() { Title = "Loops", Year = 2025, VenueType = "Journals", ReferenceNumber = 1, ApaCitation = "Ana (2025). Loops." } };
        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchive(Guid.NewGuid(), report, records)));
        using var reader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    [Fact]
    public void ARejectedDiagramIsLeftOutOfMainTexWithANote()
    {
        string tex = MainTex(new PrismaReport
        {
            TitleItem = "Agent loops",
            SynthesisResultsItem = "Overview.[TIKZ_START]\\node (a) {A};\\openin1=/etc/passwd \\read1 to \\x[TIKZ_END]",
        });
        Assert.DoesNotContain("Grounded Architectural Synthesis System Model Diagram", tex);
        Assert.Contains("% A diagram the model wrote was left out: it did not pass the check in code (diagram-check.json).", tex);
        Assert.DoesNotContain("/etc/passwd", tex);
        Assert.DoesNotContain(@"\openin", tex);
    }

    public static TheoryData<string> LatexAttacks() => new()
    {
        @"\input{/etc/passwd}", @"\immediate\write18{rm -rf /}", @"\openin1=/etc/passwd", @"\catcode`\@=11",
        @"\def\x{\input}", "^^5cinput{/etc/passwd}", @"\csname input\endcsname",
    };

    // The attack set's LaTeX cases (issue #538): text from the model or a source in the title, the abstract, the
    // synthesis and an answer comes out as characters, never as a command.
    [Theory]
    [MemberData(nameof(LatexAttacks))]
    public void LatexInModelAndSourceTextComesOutEscaped(string attack)
    {
        string tex = MainTex(new PrismaReport
        {
            TitleItem = "Loops " + attack, AbstractItem = "We reviewed " + attack, SynthesisResultsItem = "Loops recover " + attack + " [1].",
            ResearchAnswers = { new ResearchAnswer { Number = 1, Question = "Why " + attack + "?", Answer = "Because " + attack + "." } },
        });
        foreach (string command in new[] { @"\input{", @"\write18", @"\openin1", @"\catcode`", @"\def\x", "^^5c", @"\csname" })
            Assert.DoesNotContain(command, tex);
        Assert.Contains(attack.Contains('\\') ? @"\textbackslash" : @"\textasciicircum", tex);
    }
}

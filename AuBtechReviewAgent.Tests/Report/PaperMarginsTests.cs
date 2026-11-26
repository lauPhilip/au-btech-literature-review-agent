using System.Diagnostics;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Even margins in main.tex, for both kinds of review (they share <see cref="PaperLatex.Build"/>): the same white space
/// on all four sides, and nothing that runs past the right margin. Long web addresses, DOIs and commit hashes were the
/// cause of every overfull line in real papers, so they get break points (<see cref="LatexBreaks"/>).
/// </summary>
public class PaperMarginsTests
{
    private static ReviewPaper LongWordsPaper() => new()
    {
        KindLabel = "AI-GENERATED SYSTEMATIC LITERATURE REVIEW",
        Title = "Margins",
        Abstract = "An abstract.",
        Rationale = "See https://web.archive.org/web/20261123185037/https://github.com/ashutosh160798/context-scope for the source.",
        Declarations = { new("Availability", "The code of the tool (version 1.0.0+40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961) is open.") },
        References =
        {
            "www.anthropic.com (2025). Effective context engineering for AI agents. https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents (accessed 2026-11-23; archived at https://web.archive.org/web/20261123185634/https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)",
            "Ana (2025). A study. *Journal*. https://doi.org/10.1109/ASEW67777.2025.00063",
        },
    };

    [Fact]
    public void ThePreambleSetsTheSameMarginOnAllFourSides()
    {
        string tex = PaperLatex.Build(new ReviewPaper { Title = "T" });

        Assert.Contains(@"\usepackage[a4paper,margin=20mm,includefoot]{geometry}", tex);
        Assert.DoesNotContain("margin=0.8in", tex);
        Assert.Contains(@"\setlength{\emergencystretch}{2em}", tex);
        Assert.Contains(@"\usepackage{adjustbox}", tex);
        // The abstract is LaTeX's own environment, inside the margins (issue #537; before, a box pushed into them).
        Assert.Contains(@"\begin{abstract}", tex);
        Assert.Contains(@"\usepackage{xurl}", tex);
    }

    [Fact]
    public void ALongAddressIsALinkThatMayBreakAnywhere()
    {
        string escaped = PaperLatex.Escape("https://github.com/agentscope-ai/agentscope_runtime?tab=readme#start");

        // \url takes the address as it is and xurl may break it anywhere; "#" and "%" carry a backslash, so the link
        // also works inside a table or a caption.
        Assert.Equal(@"\url{https://github.com/agentscope-ai/agentscope_runtime?tab=readme\#start}", escaped);
    }

    [Fact]
    public void AHashOrTimeStampMayBreakEverySixCharacters()
    {
        Assert.Equal(@"1.\allowbreak{}0.\allowbreak{}0+40b00a\allowbreak{}0c59f6\allowbreak{}b49e12", LatexBreaks.Allow("1.0.0+40b00a0c59f6b49e12"));
        Assert.Equal(@"web/\allowbreak{}202611\allowbreak{}231856\allowbreak{}34/\allowbreak{}https:", LatexBreaks.Allow("web/20261123185634/https:"));
    }

    [Theory]
    [InlineData("A short sentence with https://a.org in it.")] // words under 20 characters stay as they are
    [InlineData(@"An escaped caret: 10\textasciicircum123456789 here")] // command names are never split
    [InlineData("Internationalisationsprozesse are long words without digits or separators")]
    public void ShortWordsAndCommandNamesAreLeftAlone(string text)
    {
        Assert.Equal(text, LatexBreaks.Allow(text));
    }

    [Fact]
    public void TheBreakPointsPrintNothing()
    {
        string tex = PaperLatex.Build(LongWordsPaper());

        Assert.Contains(@"\bibitem{ref2} Ana (2025). A study.\ \textit{Journal}.\ \url{https://doi.org/10.1109/ASEW67777.2025.00063}", tex);
        Assert.Contains("https://doi.org/10.1109/ASEW67777.2025.00063", tex.Replace(LatexBreaks.Break, ""));
    }

    /// <summary>Compiles a paper full of long words with pdflatex, when it is installed (not on CI), and finds no overfull line.</summary>
    [Fact]
    public void APaperWithLongAddressesCompilesWithoutOverfullLines()
    {
        string dir = Path.Join(Path.GetTempPath(), "margins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Join(dir, "main.tex"), PaperLatex.Build(LongWordsPaper()));
            ProcessStartInfo start = new("pdflatex", "-interaction=nonstopmode -halt-on-error main.tex") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            Process? process;
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { return; } // no pdflatex here: the markup tests above still hold
            if (process == null) return;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000)) { process.Kill(); return; }
            string log = File.ReadAllText(Path.Join(dir, "main.log"));
            if (log.Contains("File `", StringComparison.Ordinal) && log.Contains("not found", StringComparison.Ordinal)) return; // a package is missing
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain(@"Overfull \hbox", log);
        }
        finally
        {
            TestFolders.TryDelete(dir);
        }
    }
}

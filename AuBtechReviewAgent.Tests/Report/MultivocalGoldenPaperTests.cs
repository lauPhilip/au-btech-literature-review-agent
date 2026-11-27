using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #539: the multivocal paper, line for line. The module kit's fake grey literature review (ModuleKit) writes
/// its main.tex, which is compared with Golden/multivocal-main.tex as the systematic one is (ReviewPaperTests). Dates,
/// run ids, fingerprints and the tool's version change from run to run, so they are blanked. CI compiles both golden
/// files with pdflatex (the "Compile main.tex" job), so a paper that stops compiling cannot pass.
/// </summary>
public class MultivocalGoldenPaperTests
{
    private static string GoldenPath([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", "multivocal-main.tex");

    /// <summary>A main.tex with what changes between runs blanked.</summary>
    internal static string Blank(string tex)
    {
        tex = tex.Replace("\r\n", "\n");
        tex = Regex.Replace(tex, @"\d{4}-\d{2}-\d{2}([ T]\d{2}:\d{2}(:\d{2})?(\.\d+)?Z?)?( UTC)?", "DATE");
        tex = Regex.Replace(tex, @"\b\d{1,2} (?:January|February|March|April|May|June|July|August|September|October|November|December) \d{4}\b", "DAY");
        tex = Regex.Replace(tex, @"\b(?:January|February|March|April|May|June|July|August|September|October|November|December) \d{4}\b", "MONTH");
        tex = Regex.Replace(tex, @"\(run [^)]*\)", "(run RUNID)");
        tex = Regex.Replace(tex, @"the seed [0-9a-f]{8}\b", "the seed SEED"); // the audit sample's random seed
        tex = Regex.Replace(tex, @"web\.archive\.org/web/\d{14}", "web.archive.org/web/TIMESTAMP");
        tex = Regex.Replace(tex, @"[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}", "RUNID");
        tex = Regex.Replace(tex, @"(?:[0-9a-f]|\\allowbreak\{\}){16,}", "FINGERPRINT"); // with its break points
        tex = Regex.Replace(tex, @"% Run: [^;]+; tool: .*", "% Run: RUN; tool: TOOL.");
        tex = Regex.Replace(tex, @"version [^ ]+\)", "version VERSION)");
        return Regex.Replace(tex, @"This run used version .*?(?=\s+The run archive)", "This run used a version of the tool.");
    }

    [Fact]
    public async Task TheMultivocalReviewWritesTheSameMainTexAsBefore()
    {
        var run = await ModuleKit.RunAsync("multivocal");
        string tex = Blank(ModuleKit.Text(run.Archive[MultivocalPaper.MainTexFile]));
        Assert.Contains(@"\begin{document}", tex);
        Assert.Contains(@"\end{document}", tex);

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tex);
        }
        string[] expected = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'), actual = tex.Split('\n');
        int line = Enumerable.Range(0, Math.Max(expected.Length, actual.Length))
            .FirstOrDefault(i => i >= expected.Length || i >= actual.Length || expected[i] != actual[i], -1);
        Assert.True(line < 0,
            $"main.tex of the multivocal review differs from Golden/multivocal-main.tex at line {line + 1}:\n" +
            $"  golden: {(line >= 0 && line < expected.Length ? expected[line] : "(end of file)")}\n" +
            $"  now:    {(line >= 0 && line < actual.Length ? actual[line] : "(end of file)")}\n" +
            "Only a deliberate change to the paper may change it, and then the golden file is regenerated in the same commit: " +
            "run this test with UPDATE_GOLDEN=1, look at the diff and commit it with the change.");
    }
}

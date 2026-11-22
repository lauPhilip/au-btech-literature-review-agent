using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// No source file over 300 lines (issue #235). The files that were longer when the rule came in are listed with
/// their length then; each may only shrink, and comes off the list once it is split under the limit.
/// </summary>
public class FileLengthTests
{
    private const int Limit = 300;

    private static readonly string[] Roots = { "AuBtechReviewAgent", "AuBtechReviewAgent.Tests", "tools" };
    private static readonly string[] Extensions = { ".cs", ".razor", ".js", ".mjs", ".css" };
    private static readonly string[] Skipped = { "/bin/", "/obj/", "/node_modules/", "/wwwroot/lib/", "/wwwroot/css/tailwind.css" };

    private static readonly Dictionary<string, int> LongerWhenTheRuleCameIn = new()
    {
        ["AuBtechReviewAgent.Tests/Modules/MultivocalPaperTests.cs"] = 321,
        ["AuBtechReviewAgent.Tests/Modules/MultivocalRunnerTests.cs"] = 315,
        ["AuBtechReviewAgent.Tests/Modules/MultivocalSynthesisTests.cs"] = 362,
        ["AuBtechReviewAgent.Tests/Pipeline/PipelineFeatureTests.cs"] = 328,
        ["AuBtechReviewAgent.Tests/Report/ReviewPaperTests.cs"] = 322,
        ["AuBtechReviewAgent.Tests/Sources/OpenSourceAndSafetyTests.cs"] = 621,
        ["AuBtechReviewAgent.Tests/Synthesis/ThematicSynthesisTests.cs"] = 341,
        ["AuBtechReviewAgent/Components/Pages/Home.razor"] = 1397,
        ["AuBtechReviewAgent/Components/Pages/Metrics.razor"] = 346,
        ["AuBtechReviewAgent/Components/Pages/SpecMatrix.razor"] = 1364,
        ["AuBtechReviewAgent/Evidence/StudyExtractor.cs"] = 314,
        ["AuBtechReviewAgent/Llm/LlmProvider.cs"] = 329,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalExtraction.cs"] = 344,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalMap.cs"] = 366,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalPaperParts.cs"] = 425,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalPlan.cs"] = 367,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalQuality.cs"] = 501,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalReport.cs"] = 542,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalRunner.cs"] = 342,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalScreening.cs"] = 375,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalSynthesis.cs"] = 798,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalWriting.cs"] = 523,
        ["AuBtechReviewAgent/Modules/Multivocal/Pages/MultivocalPaperPage.razor"] = 413,
        ["AuBtechReviewAgent/Modules/Multivocal/Pages/MultivocalStart.razor"] = 1567,
        ["AuBtechReviewAgent/Modules/Multivocal/WebPages/PageFetcher.cs"] = 307,
        ["AuBtechReviewAgent/Pipeline/PrismaReviewEngine.cs"] = 549,
        ["AuBtechReviewAgent/Pipeline/ReviewModels.cs"] = 385,
        ["AuBtechReviewAgent/Report/MethodsSectionWriter.cs"] = 419,
        ["AuBtechReviewAgent/Report/PrismaReviewEngine.Archive.cs"] = 491,
        ["AuBtechReviewAgent/Report/PrismaReviewEngine.Report.cs"] = 983,
        ["AuBtechReviewAgent/Runs/RunMetrics.cs"] = 369,
        ["AuBtechReviewAgent/Screening/PrismaReviewEngine.Screening.cs"] = 483,
        ["AuBtechReviewAgent/Search/PrismaReviewEngine.Search.cs"] = 380,
        ["AuBtechReviewAgent/Synthesis/ReviewArtifact.cs"] = 335,
        ["AuBtechReviewAgent/Synthesis/ReviewWriter.cs"] = 817,
        ["AuBtechReviewAgent/Synthesis/ThematicSynthesis.cs"] = 495,
        ["AuBtechReviewAgent/Verification/CitationSupportChecker.cs"] = 496,
    };

    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root;
    }

    private static IEnumerable<(string Path, int Lines)> SourceFiles(string root) =>
        Roots.Select(r => Path.Join(root, r)).Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(file => Extensions.Contains(Path.GetExtension(file)) && !Skipped.Any(s => ("/" + file).Contains(s, StringComparison.Ordinal)))
            .Select(file => (file, File.ReadAllLines(Path.Join(root, file)).Length));

    [Fact]
    public void NoSourceFileIsLongerThan300Lines()
    {
        string? root = Root();
        if (root == null) return; // not run from the repository (packaged tests)

        var tooLong = SourceFiles(root)
            .Where(f => f.Lines > Limit && !LongerWhenTheRuleCameIn.ContainsKey(f.Path))
            .Select(f => $"{f.Path} ({f.Lines} lines)")
            .ToList();

        Assert.True(tooLong.Count == 0, "Split these files under 300 lines: " + string.Join(", ", tooLong));
    }

    [Fact]
    public void TheListedLongFilesOnlyShrink()
    {
        string? root = Root();
        if (root == null) return;

        var lengths = SourceFiles(root).ToDictionary(f => f.Path, f => f.Lines);
        var grown = LongerWhenTheRuleCameIn
            .Where(kv => lengths.TryGetValue(kv.Key, out int now) && now > kv.Value)
            .Select(kv => $"{kv.Key} ({lengths[kv.Key]} lines, at most {kv.Value})")
            .ToList();

        Assert.True(grown.Count == 0, "These long files grew; split them instead: " + string.Join(", ", grown));
    }
}

using System.Diagnostics;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// No file in the repository over 300 lines (issue #235): code, tests, docs, workflows, scripts and data alike.
/// The files git tracks are checked, so run folders and build output are never counted. The exceptions are files
/// nobody writes by hand. The files still longer than the limit are listed with their length when the rule came in;
/// each may only shrink, and comes off the list once it is split under the limit.
/// </summary>
public class FileLengthTests
{
    private const int Limit = 300;

    private static readonly string[] Binary = { ".png", ".jpg", ".gif", ".ico", ".woff2", ".pdf" };

    // Downloaded SCImago rankings, the generated npm lock file, vendored libraries and the compiled stylesheet.
    private static readonly string[] Exceptions =
    {
        "AuBtechReviewAgent/ScimagoData/", "AuBtechReviewAgent/package-lock.json", "/wwwroot/lib/", "/wwwroot/css/tailwind.css",
    };

    private static readonly Dictionary<string, int> LongerWhenTheRuleCameIn = new()
    {
        ["AuBtechReviewAgent/Components/Pages/Home.razor"] = 1397,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalExtraction.cs"] = 344,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalMap.cs"] = 366,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalPaperParts.cs"] = 425,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalPlan.cs"] = 367,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalQuality.cs"] = 501,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalRunner.cs"] = 342,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalScreening.cs"] = 375,
        ["AuBtechReviewAgent/Modules/Multivocal/MultivocalWriting.cs"] = 523,
        ["AuBtechReviewAgent/Modules/Multivocal/Pages/MultivocalPaperPage.razor"] = 413,
        ["AuBtechReviewAgent/Modules/Multivocal/WebPages/PageFetcher.cs"] = 307,
    };

    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root;
    }

    /// <summary>Every file git tracks, with its length; null when git or the repository is not there (packaged tests).</summary>
    private static List<(string Path, int Lines)>? TrackedFiles()
    {
        string? root = Root();
        if (root == null) return null;

        var git = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "-C", root, "ls-files", "-z" }) git.ArgumentList.Add(arg);
        string listing;
        try
        {
            using var process = Process.Start(git)!;
            listing = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git is not installed
        }

        return listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(file => !Binary.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .Where(file => !Exceptions.Any(e => ("/" + file).Contains(e.StartsWith('/') ? e : "/" + e, StringComparison.Ordinal)))
            .Where(file => File.Exists(Path.Join(root, file)))
            .Select(file => (file, File.ReadAllLines(Path.Join(root, file)).Length))
            .ToList();
    }

    [Fact]
    public void NoFileIsLongerThan300Lines()
    {
        var files = TrackedFiles();
        if (files == null) return;

        var tooLong = files
            .Where(f => f.Lines > Limit && !LongerWhenTheRuleCameIn.ContainsKey(f.Path))
            .Select(f => $"{f.Path} ({f.Lines} lines)")
            .ToList();

        Assert.True(tooLong.Count == 0, "Split these files under 300 lines: " + string.Join(", ", tooLong));
    }

    [Fact]
    public void TheListedLongFilesOnlyShrink()
    {
        var files = TrackedFiles();
        if (files == null) return;

        var lengths = files.ToDictionary(f => f.Path, f => f.Lines);
        var grown = LongerWhenTheRuleCameIn
            .Where(kv => lengths.TryGetValue(kv.Key, out int now) && now > kv.Value)
            .Select(kv => $"{kv.Key} ({lengths[kv.Key]} lines, at most {kv.Value})")
            .ToList();

        Assert.True(grown.Count == 0, "These long files grew; split them instead: " + string.Join(", ", grown));
    }

    [Fact]
    public void TheCheckSeesDocsWorkflowsAndScriptsAsWellAsCode()
    {
        var files = TrackedFiles();
        if (files == null) return;

        var paths = files.Select(f => f.Path).ToHashSet();
        Assert.Contains("README.md", paths);
        Assert.Contains(".github/workflows/ci.yml", paths);
        Assert.Contains("tools/ReleaseCheck/release_check.py", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("AuBtechReviewAgent/ScimagoData/", StringComparison.Ordinal));
        Assert.DoesNotContain("AuBtechReviewAgent/package-lock.json", paths);
    }
}

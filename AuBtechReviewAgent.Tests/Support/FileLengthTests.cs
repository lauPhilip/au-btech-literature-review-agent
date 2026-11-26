using System.Diagnostics;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// No file in the repository over 300 lines (issue #235): code, tests, docs, workflows, scripts and data alike.
/// The files git tracks are checked, so run folders and build output are never counted. The exceptions are files
/// nobody writes by hand. Every file that was longer when the rule came in has been split, so the rule has no others.
/// </summary>
public class FileLengthTests
{
    private const int Limit = 300;

    private static readonly string[] Binary = { ".png", ".jpg", ".gif", ".ico", ".woff2", ".pdf" };

    // Downloaded SCImago rankings, the generated npm lock file, vendored libraries, the compiled stylesheet and the
    // golden files the tests write (UPDATE_GOLDEN=1), such as a whole main.tex.
    private static readonly string[] Exceptions =
    {
        "AuBtechReviewAgent/ScimagoData/", "AuBtechReviewAgent/package-lock.json", "/wwwroot/lib/", "/wwwroot/css/tailwind.css",
        "AuBtechReviewAgent.Tests/Report/Golden/",
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
            .Where(f => f.Lines > Limit)
            .Select(f => $"{f.Path} ({f.Lines} lines)")
            .ToList();

        Assert.True(tooLong.Count == 0, "Split these files under 300 lines: " + string.Join(", ", tooLong));
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

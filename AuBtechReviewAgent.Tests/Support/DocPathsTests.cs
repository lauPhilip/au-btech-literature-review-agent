using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The docs name the code as it is (issue #257): every file or folder that a Markdown page names in backticks exists
/// in the repository. Names of files a run writes (run.json, main.tex) are not repository files, so a bare name is
/// only checked when it has a code extension, and a path only when its first folder is one of the repository's.
/// A partial class may be named by its last part: `ReportCitations.cs` for `PrismaReviewEngine.ReportCitations.cs`.
/// The changelog is history and is left out, as are the vendored libraries' own READMEs.
/// </summary>
public class DocPathsTests
{
    private static readonly string[] CodeExtensions =
        { ".cs", ".razor", ".js", ".mjs", ".css", ".py", ".yml", ".csproj", ".svg", ".png", ".gif", ".htm", ".ps1", ".sh" };

    // Names the docs use on purpose for a file that does not exist: the example in "Adding a database".
    private static readonly string[] Placeholders = { "XyzSource.cs" };

    private static List<string>? TrackedFiles(out string root)
    {
        root = AppContext.BaseDirectory;
        string? dir = root;
        while (dir != null && !File.Exists(Path.Join(dir, "AuBtechReviewAgent.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return null;
        root = dir;
        var git = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "-C", dir, "ls-files", "-z" }) git.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(git)!;
            string listing = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? listing.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git is not installed
        }
    }

    [Fact]
    public void EveryFileTheDocsNameExists()
    {
        var files = TrackedFiles(out string root);
        if (files == null) return; // not run from a git checkout (packaged tests)

        var names = files.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var dirs = files.SelectMany(f =>
        {
            var parts = f.Split('/');
            return Enumerable.Range(1, parts.Length - 1).Select(i => string.Join('/', parts.Take(i)));
        }).ToHashSet(StringComparer.Ordinal);
        var dirNames = dirs.Select(d => d[(d.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal);

        bool Exists(string name)
        {
            name = name.TrimEnd('/');
            if (files.Contains(name) || dirs.Contains(name)) return true;
            if (files.Any(f => f.EndsWith("/" + name, StringComparison.Ordinal)) || dirs.Any(d => d.EndsWith("/" + name, StringComparison.Ordinal))) return true;
            string extension = Path.GetExtension(name);
            if (extension.Length == 0)
                return name.Contains('/')
                    ? files.Any(f => CodeExtensions.Any(e => f == name + e || f.EndsWith("/" + name + e, StringComparison.Ordinal)))
                    : dirNames.Contains(name);
            string file = Path.GetFileName(name);
            return names.Contains(file) || names.Any(n => n!.EndsWith("." + file, StringComparison.Ordinal));
        }

        var stale = new List<string>();
        foreach (string doc in files.Where(f => f.EndsWith(".md", StringComparison.Ordinal) && f != "CHANGELOG.md" && !f.Contains("/wwwroot/lib/")))
        {
            var lines = File.ReadAllLines(Path.Join(root, doc));
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in Regex.Matches(lines[i], @"`([^`\s]+)`"))
                {
                    string name = m.Groups[1].Value.TrimEnd('.', ',', ';', ':');
                    if (name.StartsWith("http", StringComparison.Ordinal) || "/.$-~".Contains(name[0]) || Regex.IsMatch(name, @"[*<>{}()=?&]")) continue;
                    if (Placeholders.Contains(name)) continue;
                    if (name.Contains('/'))
                    {
                        string first = name.Split('/')[0];
                        if (!dirNames.Contains(first) && !names.Contains(first)) continue; // a path in a run folder, not the repository
                    }
                    else if (!CodeExtensions.Contains(Path.GetExtension(name))) continue; // a file a run writes, such as run.json
                    if (!Exists(name)) stale.Add($"{doc}:{i + 1} names `{name}`");
                }
        }

        Assert.True(stale.Count == 0, "These files are named in the docs but are not in the repository:\n" + string.Join("\n", stale));
    }
}

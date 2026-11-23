using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// tools/test-scope.sh (issue #394) picks the tests a change can affect. These tests feed it lists of changed paths
/// and check what it picks against the test folders as they are, read here independently of the script. They run
/// where bash and git are found (CI, Linux, macOS) and pass quietly elsewhere.
/// </summary>
public class TestScopeTests
{
    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return null;
        string git = Path.Join(root, ".git");
        return Directory.Exists(git) || File.Exists(git) ? root : null; // a worktree's .git is a file
    }

    /// <summary>The script's answer for these changed paths, or null when it cannot run here.</summary>
    private static string? Scope(params string[] paths)
    {
        string? root = Root();
        if (root == null || OperatingSystem.IsWindows()) return null;
        var bash = new ProcessStartInfo("bash")
        {
            WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (string arg in new[] { "tools/test-scope.sh", "--paths" }) bash.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(bash)!;
            process.StandardInput.Write(string.Join('\n', paths) + "\n");
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "test-scope.sh failed: " + error.Result);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // bash is not installed
        }
    }

    private static HashSet<string> Classes(string filter) =>
        Regex.Matches(filter, @"FullyQualifiedName~\.(\w+)\.").Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>The test classes declared in a test folder's files that hold tests.</summary>
    private static HashSet<string> TestClassesIn(string folder) =>
        Directory.GetFiles(Path.Join(Root()!, "AuBtechReviewAgent.Tests", folder), "*.cs")
            .Select(File.ReadAllText)
            .Where(text => text.Contains("[Fact") || text.Contains("[Theory"))
            .SelectMany(text => Regex.Matches(text, @"^public (?:sealed )?(?:partial )?class (\w+)", RegexOptions.Multiline))
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

    private static readonly string[] Always = { "ModuleKitTests", "FileLengthTests", "InjectionAttackSetTests" };

    [Fact]
    public void DocsAloneRunNoTests()
    {
        string? scope = Scope("docs/wiki/07-extending-and-testing.md", "README.md", "AuBtechReviewAgent/Search/README.md", "LICENSE");
        if (scope == null) return;
        Assert.Equal("NONE", scope);
    }

    [Theory]
    [InlineData("AuBtechReviewAgent/Llm/LlmJson.cs")]
    [InlineData("AuBtechReviewAgent/Pipeline/ReviewModule.cs")]
    [InlineData("AuBtechReviewAgent/Verification/Grounding.cs")]
    [InlineData("AuBtechReviewAgent/Synthesis/Anything.cs")]
    [InlineData("AuBtechReviewAgent/Report/Anything.cs")]
    [InlineData("AuBtechReviewAgent/Program.cs")]
    [InlineData("AuBtechReviewAgent/AuBtechReviewAgent.csproj")]
    [InlineData("AuBtechReviewAgent.Tests/AuBtechReviewAgent.Tests.csproj")]
    [InlineData("AuBtechReviewAgent.Tests/Support/FakeChatService.cs")]
    [InlineData("Directory.Build.props")]
    [InlineData("somewhere/new.txt")]
    public void SharedOrUnknownFilesRunTheWholeSuite(string path)
    {
        string? scope = Scope("docs/wiki/a.md", path);
        if (scope == null) return;
        Assert.Equal("ALL", scope);
    }

    [Fact]
    public void ACodeFolderRunsItsTestFolderAndTheAlwaysSet()
    {
        string? scope = Scope("AuBtechReviewAgent/Sources/SourceCatalog.cs");
        if (scope == null) return;
        var picked = Classes(scope);

        Assert.Superset(TestClassesIn("Sources"), picked);
        Assert.Superset(TestClassesIn("Security"), picked);
        Assert.Superset(Always.ToHashSet(), picked);
        Assert.DoesNotContain("SecondRepairPassTests", picked);
        Assert.Empty(TestClassesIn("Web").Intersect(picked));
    }

    [Fact]
    public void ComponentsRunTheWebTests()
    {
        string? scope = Scope("AuBtechReviewAgent/Components/Pages/About.razor");
        if (scope == null) return;
        Assert.Superset(TestClassesIn("Web"), Classes(scope));
        Assert.Empty(TestClassesIn("Search").Intersect(Classes(scope)));
    }

    [Fact]
    public void AModuleRunsTheTestsThatNameItsTypes()
    {
        string? scope = Scope("AuBtechReviewAgent/Modules/Multivocal/GreySources/GreySource.cs");
        if (scope == null) return;
        var picked = Classes(scope);

        Assert.Superset(new HashSet<string> { "MultivocalPlanTests", "GreySourceTests", "MultivocalPagesTests" }, picked);
        Assert.Superset(Always.ToHashSet(), picked);
        Assert.Empty(TestClassesIn("Search").Intersect(picked));
    }

    [Fact]
    public void ATestFileRunsItselfAndTheTestsThatUseItsClasses()
    {
        string? scope = Scope("AuBtechReviewAgent.Tests/Web/PageSource.cs");
        if (scope == null) return;
        var picked = Classes(scope);
        var users = Directory.GetFiles(Path.Join(Root()!, "AuBtechReviewAgent.Tests"), "*Tests.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bPageSource\b"))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();

        Assert.NotEmpty(users);
        Assert.All(users, user => Assert.Contains(user, picked));
    }

    [Fact]
    public void WorkflowsRunOnlyTheAlwaysSetAndDocsWithCodeAddTheDocPathCheck()
    {
        string? workflow = Scope(".github/workflows/ci.yml");
        string? mixed = Scope(".github/workflows/ci.yml", "docs/wiki/a.md");
        if (workflow == null || mixed == null) return;

        Assert.Superset(Always.ToHashSet(), Classes(workflow));
        Assert.DoesNotContain("DocPathsTests", Classes(workflow));
        Assert.Contains("DocPathsTests", Classes(mixed));
    }
}

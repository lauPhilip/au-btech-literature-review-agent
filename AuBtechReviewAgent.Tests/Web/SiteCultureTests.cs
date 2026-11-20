using System.Globalization;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Issue #134: the site formats numbers like the paper, even on a server set to Danish.</summary>
public class SiteCultureTests
{
    [Fact]
    public void ThreadsStartedAfterApplyFormatKappaWithAPoint()
    {
        var before = (CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture, CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            // Not set to Danish here: the default is process-wide, and other tests run at the same time.
            SiteCulture.Apply();

            string onThisThread = 0.63.ToString("0.00");
            string onANewThread = "";
            var thread = new Thread(() => onANewThread = $"{0.63:0.00} {1234:N0}");
            thread.Start();
            thread.Join();

            Assert.Same(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.Same(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentUICulture);
            Assert.Equal("0.63", onThisThread);
            Assert.Equal("0.63 1,234", onANewThread);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = before.Item1;
            CultureInfo.DefaultThreadCurrentUICulture = before.Item2;
            CultureInfo.CurrentCulture = before.Item3;
            CultureInfo.CurrentUICulture = before.Item4;
        }
    }

    [Fact]
    public void ProgramAppliesTheSiteCultureBeforeBuildingTheApp()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository (packaged tests)
        string program = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Program.cs"));

        int apply = program.IndexOf("SiteCulture.Apply();", StringComparison.Ordinal);
        Assert.True(apply >= 0, "Program.cs calls SiteCulture.Apply()");
        Assert.True(apply < program.IndexOf("WebApplication.CreateBuilder(args)", StringComparison.Ordinal), "before the builder is created");
    }
}

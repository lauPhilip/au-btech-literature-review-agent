using System.Text.Json;
using AuBtechReviewAgent;
using AuBtechReviewAgent.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The run page and the report give one count of checked citations (run 596541db said 109 on the run page and 113 in
/// the report): both read citation-audit.json with the reader's edits applied, and neither counts the removed ones.
/// </summary>
public class CitationCountTests
{
    private static CitationSupportResult Check(string field, int sentence, int reference, string verdict) =>
        new() { Field = field, SentenceIndex = sentence, Reference = reference, Verdict = verdict, Sentence = $"Claim [{reference}]." };

    /// <summary>A run folder with five checks, two removed citations and one edit that adds a citation to a sentence.</summary>
    private static string Folder(out ReviewStats stats)
    {
        string folder = Path.Join(Path.GetTempPath(), "traceableai-counts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var checks = new List<CitationSupportResult>
        {
            Check("synthesis", 0, 1, CitationSupportChecker.Supported), Check("synthesis", 1, 2, CitationSupportChecker.Partial),
            Check("synthesis", 2, 3, CitationSupportChecker.NotSupported), Check("discussion", 0, 1, CitationSupportChecker.Supported),
            Check("discussion", 1, 2, CitationSupportChecker.Supported),
        };
        File.WriteAllText(Path.Join(folder, "citation-audit.json"), JsonSerializer.Serialize(new
        {
            SupportChecks = checks,
            RemovedCitations = new[] { new { Reference = 4 }, new { Reference = 5 } },
        }));
        var edit = new ReportEdit(1, ReportEdit.Edited, "discussion", 1, "Claim [2].", "Claim [2] and [3].", DateTime.UtcNow, "owner",
            new List<CitationSupportResult> { Check("discussion", 1, 2, CitationSupportChecker.Supported), Check("discussion", 1, 3, CitationSupportChecker.Supported) },
            "large", "1.0.0");
        File.WriteAllText(Path.Join(folder, ReportEditLog.FileName), JsonSerializer.Serialize(new ReportEditLog { Edits = { edit } }));
        // The run's figures were written before the edit: five checked, two removed.
        stats = new ReviewStats
        {
            ProcessingStage = PrismaReviewEngine.StageComplete, Included = 3, TotalIdentified = 20,
            CitationsChecked = 5, CitationsSupported = 3, CitationsPartiallySupported = 1, CitationsNotSupported = 1, CitationsRemoved = 2,
        };
        return folder;
    }

    [Fact]
    public void TheCountLeavesOutRemovedCitationsAndFollowsEdits()
    {
        string folder = Folder(out var stats);
        try
        {
            var tally = VerdictTally.ForRun(folder, stats);

            Assert.Equal(6, tally.Checked); // five from the run, one more from the edit
            Assert.Equal(8, tally.Total);   // the bar also shows the two removed
            Assert.Equal(2, tally.Removed);
            Assert.Equal(4, tally.Supported);
            Assert.Equal(VerdictTally.From(stats).Checked, VerdictTally.ForRun(Path.Join(folder, "missing"), stats).Checked);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task TheRunPageAndTheReportSayTheSameNumber()
    {
        string folder = Folder(out var stats);
        try
        {
            var tally = VerdictTally.ForRun(folder, stats);
            string card = await RenderAsync(new() { ["Stats"] = stats, ["RunId"] = Guid.NewGuid(), ["Tally"] = tally });
            var state = new ReviewState { Stats = stats };
            string runLine = AuBtechReviewAgent.Components.Pages.SpecMatrix.RunLine(state, tally);

            Assert.Contains("6 citations checked against the papers", card);
            Assert.Contains("6 citations checked", runLine);
            Assert.DoesNotContain("8 citations checked", card);
            Assert.Contains("Of 8 citations: 4 supported, 1 partly supported, 1 not supported, 2 removed.", card);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<RunSummaryCard>(ParameterView.FromDictionary(parameters));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}

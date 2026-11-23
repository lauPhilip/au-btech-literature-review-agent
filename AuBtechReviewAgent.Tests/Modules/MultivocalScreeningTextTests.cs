using AuBtechReviewAgent;
using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// What the method, the flow diagram, main.tex, the abstract's facts, the statement on AI use and the funnel say about
/// targeted second screenings: the same counts everywhere, κ on the audit sample only, and the targeted agreement apart.
/// Built on <see cref="MultivocalPaperTests.Input"/> with four sources screened: three twice, one once.
/// </summary>
public class MultivocalScreeningTextTests
{
    private static ScreeningAnswer A(string decision, string confidence = "high") => new() { Decision = decision, Reasoning = "r", Confidence = confidence };

    /// <summary>a: included, also in the audit sample; b: unsure, and the second screening disagreed; c: included; d: once.</summary>
    internal static MultivocalReportInput Targeted()
    {
        var input = MultivocalPaperTests.Input();
        var s = input.Screening;
        s.AuditSeed = "3f2a91bc";
        s.AuditShare = 0.2;
        s.AuditMinimum = 10;
        s.Kappa = 0.9;
        var (a, b, c, d) = (s.Sources[0], s.Sources[1], s.Sources[2], s.Sources[3]);
        (a.First, a.Second, a.SecondReason, a.AuditSample) = (A("Included"), A("Included"), MultivocalSecondScreening.Included, true);
        (b.First, b.Second, b.SecondReason) = (A("Excluded", "medium"), A("Included"), MultivocalSecondScreening.Uncertain);
        (c.First, c.Second, c.SecondReason) = (A("Included"), A("Included"), MultivocalSecondScreening.Included);
        d.First = A("Excluded");
        return input;
    }

    [Fact]
    public void TheCountsComeFromTheFile()
    {
        var c = MultivocalSecondScreening.Count(Targeted().Screening, null);
        Assert.Equal((true, 4, 3, 1, 3, 2, 2, 1, 0, 1, 0), (c.Targeted, c.Screened, c.ScreenedTwice, c.Once, c.TargetedTotal, c.TargetedAgreed, c.ForIncluded, c.ForUncertain, c.ForFlagged, c.Audit, c.AuditOnly));
        Assert.False(MultivocalSecondScreening.Count(MultivocalPaperTests.Input().Screening, null).Targeted); // an older run, with no seed
    }

    [Fact]
    public void TheMethodSaysWhatWasScreenedTwiceAndWhyAndGivesKappaOnTheSampleOnly()
    {
        string selection = MultivocalReporter.MethodSections(Targeted()).Single(x => x.Heading.StartsWith("3.")).Blocks[0].Text;

        Assert.StartsWith("Each of the 5 sources found was screened once by the language model against the protocol's criteria (G9, G10). Following the rapid-review practice of single screening with a verified sample,", selection);
        Assert.Contains("random audit sample of 20% of its pool (at least 10), drawn with the seed 3f2a91bc", selection);
        Assert.Contains("Of the 4 sources screened, 3 were screened twice and 1 once: 3 for a reason above (2 included, 1 unsure, 0 flagged), of which 2 agreed with the first screening, and 1 in the audit sample, 0 of them for that reason alone.", selection);
        Assert.Contains("Cohen's κ was computed on the audit sample only, so the targeted second screenings do not enter it: κ = 0.90 over 1 source.", selection);
        Assert.Contains("Both screenings are the same model at temperature 0, so κ measures how stable the model's decisions are, not agreement between two people.", selection);
        Assert.DoesNotContain("screened twice by the language model", selection);

        // An older run keeps the words it had.
        string older = MultivocalReporter.MethodSections(MultivocalPaperTests.Input()).Single(x => x.Heading.StartsWith("3.")).Blocks[0].Text;
        Assert.StartsWith("Each of the 5 sources found was screened twice by the language model against the protocol's criteria, independently and with the steps in a different order (G9, G10). The two screenings agreed with Cohen's κ = 0.80.", older);
    }

    [Fact]
    public void TheFlowDiagramMainTexAndTheAbstractsFactsGiveTheSameCounts()
    {
        var input = Targeted();
        string latex = MultivocalPaperParts.DataLatex(input);
        Assert.Contains(@"(screened) {Screened: 4\\{\footnotesize of which twice: 3}};", latex);
        Assert.DoesNotContain("Screened twice", latex);
        Assert.Contains(@"(screened) {Screened twice: 4};", MultivocalPaperParts.DataLatex(MultivocalPaperTests.Input()));

        string facts = MultivocalWriter.FrontMatterFacts(input);
        Assert.Contains("Screened against the criteria: 4, each once and 3 of them a second time (3 because the first screening included them, was unsure or flagged them; 1 in a random audit sample). ", facts);

        var methods = MultivocalPaperParts.Methods(input);
        Assert.Contains(@"$\kappa$ = 0.90 over 1 source", methods[2].Latex);
        var g10 = MultivocalReporter.Write(input).Sections.SelectMany(x => x.Blocks).Where(b => b.Kind == "table").SelectMany(b => b.Rows).Single(r => r[0] == "G10");
        Assert.Equal("Every source screened once, 3 twice (3 included, unsure or flagged, 2 of them agreeing; 1 in a random audit sample); κ = 0.90 on the audit sample; 0 decisions flagged as uncertain; 0 looked at by the reviewer.", g10[2]);
    }

    [Fact]
    public void TheStatementOnAiUseSaysOnceAndTwiceAndKeepsKappaApartFromTheTargetedAgreement()
    {
        string text = MultivocalPaper.AiUse(Targeted(), new MultivocalPaperFile { Model = "large-model", WrittenUtc = new DateTime(2026, 11, 23) });

        Assert.Contains("The model was used for screening the grey sources against the criteria, once each and again when the first screening included a source, was unsure or found text that looked like instructions, or drew it for a random audit sample, scoring their quality", text);
        Assert.Contains("On the random audit sample of 1 source, the two screenings agreed with Cohen's kappa = 0.90; 2 of 3 targeted second screenings agreed with the first. Both screenings were the same model at temperature 0, so kappa measures its stability, not agreement between people. ", text);
    }

    [Fact]
    public async Task TheFunnelShowsTheSecondScreeningsAndKappaOnTheAuditSample()
    {
        var input = Targeted();
        Assert.Equal("3 twice (2 of 3 targeted agreed); κ = 0.90 on an audit sample of 1", MultivocalScreeningText.FunnelNote(input.Screening));

        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MultivocalPoolFunnel>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Grey"] = MultivocalPools.For(false, input.Ledger, input.Screening, input.Quality, input.Extraction),
            ["Formal"] = MultivocalPools.For(true, input.Ledger, input.Screening, input.Quality, input.Extraction),
            ["Kappa"] = input.Screening.Kappa, ["Screened"] = true,
        }))).ToHtmlString());

        Assert.Contains(">of which screened twice</th>", html);
        Assert.Contains(">Targeted (included, unsure, flagged): agreed</th>", html);
        Assert.Contains("2 of 3</td>", html);
        Assert.Contains(">Random audit sample</th>", html);
        Assert.Contains("Agreement on the audit sample (", html);
        Assert.DoesNotContain("Screened twice", html);
    }

    [Fact]
    public void TheProtocolNamesTheRuleAndTheSeed()
    {
        string protocol = MultivocalProtocol.Write(MultivocalPlan.Example(), Guid.Parse("3f2a91bc-0000-4000-8000-000000000001"), new DateTime(2026, 11, 23));
        Assert.Contains("The sample is drawn with the seed 3f2a91bc: the sources ranked by the SHA-256 of the seed, a bar and their address, lowest first.", protocol);
        Assert.Contains("the agreement of the other second screenings is reported apart, as counts", protocol);
    }
}

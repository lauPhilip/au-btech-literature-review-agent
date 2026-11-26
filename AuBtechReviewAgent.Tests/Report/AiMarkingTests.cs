using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;
using AuBtechReviewAgent.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The machine-readable marks that a report is AI-generated (EU AI Act Art. 50(2)): on the report pages, in main.tex of
/// both kinds of review, and in the archive's manifest.json. All are written in code by <see cref="AiMarking"/>.
/// </summary>
public class AiMarkingTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "aimark-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task AReportPageCarriesTheMetaTagsAndSchemaOrgData()
    {
        string html = await RenderAsync(new[] { "mistral-large-latest", "mistral-small-latest" });

        Assert.Contains("<meta name=\"ai-generated\" content=\"true\" />", html);
        Assert.Contains($"<meta name=\"generator\" content=\"TraceableAI {RecordingChatCompletionService.AppVersion} (Department of Business Development and Technology, Aarhus University) with the language models mistral-large-latest, mistral-small-latest\" />", html);
        Assert.Contains("<meta name=\"ai-models\" content=\"mistral-large-latest, mistral-small-latest\" />", html);
        Assert.Contains("<script type=\"application/ld+json\">", html);
        Assert.Contains("TrainedAlgorithmicMediaDigitalSource", html);
        string json = html[(html.IndexOf("{", StringComparison.Ordinal))..html.LastIndexOf("</script>", StringComparison.Ordinal)];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("TraceableAI", doc.RootElement.GetProperty("creator").GetProperty("name").GetString());
        Assert.Equal("AI-generated", doc.RootElement.GetProperty("keywords").GetString());
    }

    [Fact]
    public async Task APageWithoutRecordedModelsIsStillMarked()
    {
        string html = await RenderAsync(Array.Empty<string>());

        Assert.Contains("<meta name=\"ai-generated\" content=\"true\" />", html);
        Assert.Contains($"content=\"TraceableAI {RecordingChatCompletionService.AppVersion} (Department of Business Development and Technology, Aarhus University)\"", html);
        Assert.DoesNotContain("ai-models", html);
    }

    [Fact]
    public void TheLatexMarksEscapeAModelsName()
    {
        string tex = AiMarking.LatexMetadata("A title", new[] { "my_model#1", " my_model#1 ", null });

        Assert.Contains(@"\usepackage[hidelinks]{hyperref}", tex);
        Assert.Contains("pdftitle={A title},", tex);
        Assert.Contains(@"pdfkeywords={AI-generated, literature review, TraceableAI, my\_model\#1},", tex);
        Assert.Contains(@"pdfcreator={TraceableAI (Department of Business Development and Technology, Aarhus University) with the language model my\_model\#1},", tex);
        Assert.StartsWith("% AI-generated: this document was written and checked by TraceableAI", AiMarking.LatexComment(new[] { "m\nx" }));
        Assert.DoesNotContain("\nx", AiMarking.LatexComment(new[] { "m\nx" }).Split('\n')[0]);
    }

    [Fact]
    public async Task TheSystematicMainTexIsMarked()
    {
        string tex = await ReviewPaperTests.SystematicMainTexAsync(_root);
        AssertMarked(tex);
    }

    [Fact]
    public async Task TheMultivocalMainTexAndManifestAreMarked()
    {
        var run = await FormalKit.RunAsync(_root);
        AssertMarked(run.Writer.MainTex(run.RunId)!);
        Assert.Contains("fake-model", run.Writer.MainTex(run.RunId)!.Split('\n')[0]);

        using var zip = new ZipArchive(new MemoryStream(run.Writer.Archive(run.RunId)));
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        AssertManifestMarked(reader.ReadToEnd(), "fake-model");
    }

    [Fact]
    public void TheManifestSaysTheArchiveIsAiGeneratedAndStaysIntact()
    {
        var files = new List<(string, byte[])> { ("main.tex", Encoding.UTF8.GetBytes("tex")) };
        string manifest = RunManifest.Build(Guid.NewGuid(), "hash", "mistral-large-latest", files);

        AssertManifestMarked(manifest, "mistral-large-latest");
        Assert.True(RunManifest.ManifestIsIntact(manifest));
        using var none = JsonDocument.Parse(RunManifest.Build(Guid.NewGuid(), null, null, files));
        Assert.Equal(0, none.RootElement.GetProperty("Generator").GetProperty("Models").GetArrayLength());
    }

    private static void AssertMarked(string tex)
    {
        Assert.StartsWith("% AI-generated: this document was written and checked by TraceableAI", tex);
        Assert.Contains(@"\usepackage[hidelinks]{hyperref}", tex);
        Assert.Contains("pdfkeywords={AI-generated,", tex);
        Assert.Contains("pdfsubject={AI-generated literature review: written and checked by TraceableAI", tex);
        Assert.Contains("pdfcreator={TraceableAI", tex);
        Assert.Contains("pdfproducer={TraceableAI (AI-generated) with pdfLaTeX}", tex);
        Assert.True(tex.IndexOf("hyperref", StringComparison.Ordinal) < tex.IndexOf(@"\begin{document}", StringComparison.Ordinal));
    }

    private static void AssertManifestMarked(string manifest, string model)
    {
        using var doc = JsonDocument.Parse(manifest);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("AiGenerated").GetBoolean());
        var generator = root.GetProperty("Generator");
        Assert.Equal("TraceableAI", generator.GetProperty("Name").GetString());
        Assert.Equal(RecordingChatCompletionService.AppVersion, generator.GetProperty("Version").GetString());
        Assert.Equal(model, generator.GetProperty("Models")[0].GetString());
        Assert.StartsWith("AI-generated literature review", generator.GetProperty("Statement").GetString());
    }

    private static async Task<string> RenderAsync(IReadOnlyList<string> models)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        // Decoded, so the version's "+" reads as it is written, whatever the encoder makes of it.
        return System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AiGeneratedMeta>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Models"] = models }))).ToHtmlString()));
    }
}

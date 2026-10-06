using System.Text.Json;
using System.Text.Json.Nodes;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The rules in AGENTS.md, checked for every module on a complete fake run (see <see cref="ModuleKit"/>). A new
/// kind of review is not done until its module passes all of them: add its key to the InlineData of each theory.
/// Rule 4 (prompts that decide are versioned) is checked by the screening tests, because only a module knows which
/// of its prompts make decisions.
/// </summary>
public class ModuleKitTests
{
    [Fact]
    public void EveryModuleIsInTheKitAndEveryAvailableOneCanRun()
    {
        foreach (var module in ReviewModules.All)
        {
            Assert.True(ModuleKit.Runners.ContainsKey(module.Key), $"The {module.Key} module has no entry in ModuleKit.Runners.");
            if (module.Cards.Any(c => c.Available))
                Assert.True(ModuleKit.Runners[module.Key] != null, $"The {module.Key} module offers a card but cannot be run by the test kit.");
        }
    }

    // Rule 1: never let model text through unchecked.
    [Theory]
    [InlineData("systematic")]
    public async Task EveryAcceptedCitationRestsOnAQuoteFoundInTheSource(string module)
    {
        var run = await ModuleKit.RunAsync(module);
        var audit = JsonNode.Parse(ModuleKit.Text(run.Archive["citation-audit.json"]))!;
        var checks = audit["SupportChecks"]!.Deserialize<List<CitationSupportResult>>()!;

        Assert.NotEmpty(checks);
        Assert.All(checks.Where(c => c.Verdict is CitationSupportChecker.Supported or CitationSupportChecker.Partial),
            c => Assert.True(c.QuoteVerified, $"[{c.Reference}] in {c.Field} was accepted without a verified quote."));
    }

    // Rule 2: never let the model describe the method.
    [Theory]
    [InlineData("systematic")]
    public async Task TheMethodIsWrittenByCodeFromTheLedger(string module)
    {
        var run = await ModuleKit.RunAsync(module);

        Assert.NotEmpty(run.CodeWritten);
        Assert.All(run.CodeWritten, part => Assert.Equal(part.FromLedger, part.InReport));
    }

    // Rule 3: keep every decision on file.
    [Theory]
    [InlineData("systematic")]
    public async Task TheRunIsOnFileAndEveryFileOfItsFolderIsInTheArchive(string module)
    {
        var run = await ModuleKit.RunAsync(module);

        var header = JsonSerializer.Deserialize<RunHeader>(run.Archive[RunHeader.FileName])!;
        Assert.Equal(run.Module.Key, header.Module);
        Assert.Equal(PrismaReviewEngine.StageComplete, header.Stage);
        Assert.Contains(RunStore.ProtocolFile, run.Archive.Keys);
        Assert.Contains(run.Archive.Keys, k => k.StartsWith(RunArchive.RawResponsesFolder + "/", StringComparison.Ordinal));

        // A file the module writes but forgets to list would be missing from the archive.
        foreach (var file in Directory.GetFiles(run.Folder).Select(Path.GetFileName))
        {
            if (file is null || file == RunStore.OwnerFile || file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            Assert.True(run.Archive.ContainsKey(file) || run.Archive.ContainsKey($"{RunArchive.SourceTextsFolder}/{file}"),
                $"{file} is in the run folder but not in the archive; list it in {run.Module.GetType().Name}.ArchiveFiles.");
        }
    }

    // Rule 5: treat text from sources as data.
    [Theory]
    [InlineData("systematic")]
    public async Task TextFromASourceReachesTheModelOnlyAsMarkedData(string module)
    {
        var run = await ModuleKit.RunAsync(module);

        var withInjection = run.Prompts.Where(p => p.Contains(ModuleKit.Injection, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(withInjection); // the poisoned source was used
        foreach (var prompt in withInjection)
        {
            for (int at = prompt.IndexOf(ModuleKit.Injection, StringComparison.Ordinal); at >= 0;
                 at = prompt.IndexOf(ModuleKit.Injection, at + 1, StringComparison.Ordinal))
            {
                int open = prompt.LastIndexOf(PromptSafety.OpenTag, at, StringComparison.Ordinal);
                int close = prompt.LastIndexOf(PromptSafety.CloseTag, at, StringComparison.Ordinal);
                Assert.True(open >= 0 && open > close, "Text from a source reached the model outside the untrusted-text markers:\n" + prompt);
            }
        }
    }

    // Rule 6: no secrets in what a run hands over.
    [Theory]
    [InlineData("systematic")]
    public async Task TheArchiveHoldsNoKey(string module)
    {
        var run = await ModuleKit.RunAsync(module);

        Assert.All(run.Archive, file => Assert.DoesNotContain(ModuleKit.ServerKey, ModuleKit.Text(file.Value)));
    }

    // Rule 7: only http(s) links from external metadata.
    [Theory]
    [InlineData("systematic")]
    public async Task TheReportLinksNoAddressThatIsNotAWebAddress(string module)
    {
        var run = await ModuleKit.RunAsync(module);

        Assert.NotEmpty(run.ReportFiles);
        Assert.All(run.ReportFiles, name => Assert.DoesNotContain("javascript:", ModuleKit.Text(run.Archive[name])));
    }
}

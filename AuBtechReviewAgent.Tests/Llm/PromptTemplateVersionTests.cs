using System.Runtime.CompilerServices;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The cache keys of screening, the multivocal quality and extraction steps and the formal appraisal hold a fingerprint
/// of their prompt's fixed text, so an edit to a prompt is never answered from the cache (AGENTS.md rule 4). Each prompt
/// version is pinned to its fingerprint in Golden/prompt-versions.txt: a prompt whose text changes while its version
/// does not fails here. Golden/screening-prompts.txt shows the screening prompts of both reviews written with
/// placeholders, so a change to the steps they share is seen in one diff. UPDATE_GOLDEN=1 rewrites the screening file
/// and adds a row for a new version; it never changes the row of a version already listed.
/// </summary>
public class PromptTemplateVersionTests
{
    private static readonly Dictionary<string, Func<(string Version, string Hash)>> Prompts = new()
    {
        ["systematic-screening-first"] = () => (PrismaReviewEngine.ScreeningPromptVersion, PrismaReviewEngine.ScreeningTemplateSha256(false)),
        ["systematic-screening-second"] = () => (PrismaReviewEngine.ScreeningPromptVersion, PrismaReviewEngine.ScreeningTemplateSha256(true)),
        ["grey-screening-first"] = () => (MultivocalScreener.PromptVersion, MultivocalScreener.TemplateSha256(false, false)),
        ["grey-screening-second"] = () => (MultivocalScreener.PromptVersion, MultivocalScreener.TemplateSha256(false, true)),
        ["formal-screening-first"] = () => (MultivocalScreener.FormalPromptVersion, MultivocalScreener.TemplateSha256(true, false)),
        ["formal-screening-second"] = () => (MultivocalScreener.FormalPromptVersion, MultivocalScreener.TemplateSha256(true, true)),
        ["grey-quality"] = () => (MultivocalQualityAssessor.PromptVersion, MultivocalQualityAssessor.TemplateSha256),
        ["grey-extraction"] = () => (MultivocalExtractor.PromptVersion, MultivocalExtractor.TemplateSha256(false)),
        ["formal-extraction"] = () => (MultivocalExtractor.FormalPromptVersion, MultivocalExtractor.TemplateSha256(true)),
        ["formal-appraisal"] = () => (MultivocalQualityAssessor.FormalAppraisalVersion, StudyExtractor.MmatTemplateSha256),
        ["language-edit"] = () => (StylisticRefinerUtility.PromptVersion, StylisticRefinerUtility.TemplateSha256),
    };

    public static TheoryData<string> Names => new(Prompts.Keys);

    private static bool Update => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";
    private static string Golden(string name, [CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", name);

    [Theory]
    [MemberData(nameof(Names))]
    public void APromptsFixedTextChangesOnlyWithItsVersion(string name)
    {
        var (version, hash) = Prompts[name]();
        string path = Golden("prompt-versions.txt");
        var rows = File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(' ')).ToList()
            : new List<string[]>();
        var row = rows.FirstOrDefault(r => r[0] == name && r[1] == version);
        if (row == null && Update)
        {
            File.AppendAllText(path, $"{name} {version} {hash}\n");
            return;
        }
        Assert.True(row != null, $"{name} is at {version}, which {path} does not list. Add the row \"{name} {version} {hash}\" (UPDATE_GOLDEN=1 adds it).");
        Assert.True(row![2] == hash, $"The fixed text of the {name} prompt changed, but its version is still {version}. " +
            "Raise the version constant, so the change is on file, and add a row for the new version (UPDATE_GOLDEN=1 adds it).");
    }

    [Fact]
    public void TheScreeningPromptsOfBothReviewsAreAsOnFile()
    {
        var sections = new List<string>();
        foreach (bool second in new[] { false, true })
        {
            string screener = second ? "second screener" : "first screener";
            var systematic = PrismaReviewEngine.ScreeningTemplate(second).ToList();
            sections.Add($"=== Systematic review, {screener}, with an abstract ===\n{systematic[0]}");
            sections.Add($"=== Systematic review, {screener}, without an abstract ===\n{systematic[1]}");
            sections.Add($"=== Multivocal review, grey source, {screener} ===\n{MultivocalScreener.Template(false, second)}");
            sections.Add($"=== Multivocal review, formal source, {screener} ===\n{MultivocalScreener.Template(true, second)}");
        }
        string text = string.Join("\n\n", sections).Replace("\r\n", "\n") + "\n";
        string path = Golden("screening-prompts.txt");
        if (Update || !File.Exists(path)) File.WriteAllText(path, text);
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void ATemplateHoldsPlaceholdersNotRecordDataAndLineEndingsDoNotMatter()
    {
        string template = PrismaReviewEngine.ScreeningTemplate(false).First();
        Assert.Contains("{title}", template);
        Assert.Contains("{inclusion}", template);
        Assert.Equal(PromptTemplate.Hash("a\r\nb"), PromptTemplate.Hash("a\nb"));
        Assert.NotEqual(PromptTemplate.Hash("a\nb"), PromptTemplate.Hash("a\nc"));
    }

    [Fact]
    public void EachPromptHasItsOwnFingerprint()
    {
        var hashes = Prompts.Values.Select(p => p().Hash).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }
}

using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #461's leftover: the multivocal rationale opened on one GitHub README as if it were established fact. The front
/// matter now gets the sources that are weak evidence on their own, with the reason written in code (the appraisal and
/// design for the systematic review, the outlet tier for the multivocal review), and a soft check sends back a
/// rationale whose first sentence rests on one of them alone.
/// </summary>
public class WeakSourceTests
{
    private static StudyExtraction Study(int reference, string category, params string[] answers) => new()
    {
        ReferenceNumber = reference,
        AppraisalCategory = category,
        Appraisal = answers.Select((a, i) => new AppraisalAnswer { Id = $"{i + 1}", Answer = a }).ToList(),
    };

    [Fact]
    public void AStudyIsWeakWhenItIsNotEmpiricalHasNoTextOrFailsMostCriteria()
    {
        Assert.Equal("it is not empirical (a position paper, system description or review)", SystematicEvidence.WeakBecause(Study(1, "not_empirical")));
        Assert.Equal("it had neither an abstract nor a full text", SystematicEvidence.WeakBecause(Study(2, StudyExtractor.NotAppraised)));
        Assert.Equal("nothing was extracted from it", SystematicEvidence.WeakBecause(new StudyExtraction { ReferenceNumber = 3, Error = "down" }));
        Assert.Equal("it fails 3 of the 5 MMAT criteria", SystematicEvidence.WeakBecause(Study(4, "qualitative", "no", "no", "no", "yes", "cant_tell")));
        // "Can't tell" mostly means the abstract does not say; it is not a failure.
        Assert.Null(SystematicEvidence.WeakBecause(Study(5, "qualitative", "cant_tell", "cant_tell", "cant_tell", "no", "yes")));
        Assert.Null(SystematicEvidence.WeakBecause(null));
        var weak = SystematicEvidence.WeakSources(new[] { Study(1, "not_empirical"), Study(5, "qualitative", "yes", "yes", "yes", "yes", "yes") });
        Assert.Equal(new[] { 1 }, weak.Keys);
    }

    [Fact]
    public void AThirdTierGreySourceIsWeakAndAnAcademicOneIsNotJudged()
    {
        GreyRecord Record(string kind) => new("x", "t", "", "https://example.org", "p", "s", kind, null, new Dictionary<string, long>());
        Assert.Equal("it is a 3rd-tier grey source (code repositories and their readme files)", MultivocalEvidence.Weak(Record("code")));
        Assert.Null(MultivocalEvidence.Weak(Record("documentation")));
        Assert.Null(MultivocalEvidence.Weak(Record(MultivocalSearcher.FormalKind)));
    }

    [Fact]
    public void ARationaleThatOpensOnOneWeakSourceAsFactIsAProblem()
    {
        var weak = new Dictionary<int, string> { [1] = "it is a 3rd-tier grey source (blog post)" };
        var profile = new WritingProfile { Source = "source", Sources = "sources" };
        Assert.StartsWith("the rationale opens on [1] alone as established fact, but it is a 3rd-tier grey source (blog post).",
            ReviewWriter.WeakOpeningProblem("Most agent failures are context failures [1]. Retrieval helps [2].", weak, profile));
        Assert.Null(ReviewWriter.WeakOpeningProblem("One blog post argues that most agent failures are context failures [1].", weak, profile));
        Assert.Null(ReviewWriter.WeakOpeningProblem("Most agent failures are context failures [1, 2].", weak, profile));
        Assert.Null(ReviewWriter.WeakOpeningProblem("Retrieval helps [2]. Most failures are context failures [1].", weak, profile));
        Assert.Null(ReviewWriter.WeakOpeningProblem("Most agent failures are context failures [1].", null, profile));
    }

    [Fact]
    public async Task TheFrontMatterGetsTheWeakSourcesAndSendsBackAnOpeningOnOneOfThem()
    {
        static string Front(string rationale) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            { ["title"] = "Agent memory: a systematic review", ["abstract"] = "We read the studies.", ["rationale"] = rationale, ["objectives"] = "To map agent memory." });
        var model = new FakeChatService()
            .Returns(Front("Agents forget across sessions [2]."))
            .Returns(Front("Agents forget across sessions [1]. A position paper argues that memory is the bottleneck [2]."));
        var notes = new List<string>();

        var front = await ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent memory", "Map agent memory", "Studies: 2.",
            new[] { new SynthesisSection { Field = "synthesis-1", Heading = "Memory", Text = "Agents forget across sessions [1, 2]." } }, "", "[1] A. [2] B.", 2, notes.Add,
            weakSources: new Dictionary<int, string> { [2] = "it is not empirical (a position paper, system description or review)" });

        Assert.StartsWith("Agents forget across sessions [1].", front!.Rationale);
        Assert.Contains("WEAK EVIDENCE (from the data extraction; each of these studies is weak evidence on its own):\n[2] it is not empirical", model.Prompts[0]);
        Assert.Contains("The first sentence does not rest on one study listed under weak evidence alone", model.Prompts[0]);
        Assert.Contains("YOUR PREVIOUS ANSWER WAS REJECTED: the rationale opens on [2] alone as established fact", model.Prompts[1]);
        Assert.StartsWith("The front-matter call was sent back for a rationale that opens on one weak source (1 of 2)", Assert.Single(notes));
    }
}

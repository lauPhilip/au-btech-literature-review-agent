using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #447: in run cce1c6e4 the peer-review critique saw no facts about the studies, asserted that none of them was a
/// systematic review (false), and the revision applied it; it also asked a theme to cite a study left out of the themes.
/// The critique now gets the facts, the left-out studies and each theme's studies, and a theme revision that cites a
/// study outside its theme is rejected in code.
/// </summary>
public class PeerReviewFactsTests
{
    private static ReviewEvidence Evidence() => new()
    {
        Sources = Enumerable.Range(1, 4).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}." }).ToList(),
        Themes = new[]
        {
            new EvidenceTheme { Id = "T1", Name = "Recovery", Sources = new[] { 1, 2 } },
            new EvidenceTheme { Id = "T2", Name = "Audits", Sources = new[] { 3 } },
        },
        BaseFacts = "4 studies; designs: [3] systematic review; [1] case study; [2] experiment.",
        LeftOut = new[] { new UncodedStudy(4, "Codes left out of the themes: about vaping.") },
    };

    private static List<SynthesisSection> Sections() => new()
    {
        new SynthesisSection { Field = "synthesis-1", ThemeId = "T1", Heading = "Recovery", Text = "Supervisors restart failed steps [1, 2]." },
        new SynthesisSection { Field = "synthesis-2", ThemeId = "T2", Heading = "Audits", Text = "Audit layers log agent actions [3]." },
    };

    [Fact]
    public async Task TheCritiqueGetsTheFactsTheLeftOutStudiesAndEachThemesStudies()
    {
        var model = new FakeChatService().Returns("""{"comments":[]}""");

        await ReviewWriter.PeerReviewAsync(model, WritingProfile.Systematic, Sections(), "Recovery and audits work together [1, 3].", Evidence(), 4, 1);

        string prompt = model.Prompts.Single().Replace("\r\n", "\n");
        Assert.Contains("(a) depth: does it synthesise and compare rather than list (for a theme that rests on one study, judge only whether it reports that study faithfully", prompt);
        Assert.Contains(ReviewWriter.CritiqueFactsRule(WritingProfile.Systematic), prompt);
        Assert.Contains("the only facts about the studies and their designs; do not contradict them):\n4 studies; designs: [3] systematic review;", prompt);
        Assert.Contains("STUDIES LEFT OUT OF THE THEMES", prompt);
        Assert.Contains("[4] Codes left out of the themes: about vaping.", prompt);
        Assert.Contains("[synthesis-1] Recovery (studies of this theme: [1], [2])\nSupervisors restart", prompt);
        Assert.Contains("[synthesis-2] Audits (studies of this theme: [3]; it rests on one study)\nAudit layers", prompt);
        Assert.Contains("[discussion] Discussion\nRecovery and audits", prompt);
    }

    [Fact]
    public async Task AThemeRevisionThatCitesAStudyOutsideItsThemeIsRejected()
    {
        var model = new FakeChatService()
            .Returns("""{"comments":[{"section":"synthesis-1","severity":"medium","issue":"Study 4 is missing.","suggestion":"Integrate [4]."}]}""")
            .Returns("""{"text":"Supervisors restart failed steps [1, 2], and vaping apps do too [4]."}""");
        var sections = Sections();

        var (log, _) = await ReviewWriter.PeerReviewAsync(model, WritingProfile.Systematic, sections, "Recovery and audits work together [1, 3].", Evidence(), 4, 1);

        Assert.Contains("CURRENT SECTION (Recovery):\nSupervisors restart failed steps [1, 2].\n\nThis section reports one theme: cite only its studies, [1], [2], even where the feedback asks for others.", model.Prompts[1].Replace("\r\n", "\n"));
        var revision = Assert.Single(log.SectionRevisions);
        Assert.False(revision.Applied);
        Assert.Equal("Revision rejected: it cites [4], which is not among the studies of this theme ([1], [2]); original kept.", revision.Note);
        Assert.Equal("Supervisors restart failed steps [1, 2].", sections[0].Text);
    }

    [Fact]
    public void AStudyTheSectionAlreadyCitedStaysAllowed()
    {
        Assert.Null(ReviewWriter.OutsideTheme("A [1].", "A [1, 2].", new[] { 1, 2 }, WritingProfile.Systematic));
        Assert.Null(ReviewWriter.OutsideTheme("A [1, 5].", "A [1] and [5].", new[] { 1, 2 }, WritingProfile.Systematic));
        Assert.NotNull(ReviewWriter.OutsideTheme("A [1].", "A [1, 3].", new[] { 1, 2 }, WritingProfile.Systematic));
    }
}

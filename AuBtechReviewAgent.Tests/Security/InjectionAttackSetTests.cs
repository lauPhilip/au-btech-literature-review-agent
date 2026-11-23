using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The attack set (#373), case by case: the text is cleaned and scanned as the reviews do it, and the wrapped text that
/// would go to the model is checked. The whole runs are in InjectionAttackSetRunTests and InjectionAttackSetMultivocalTests.
/// </summary>
public class InjectionAttackSetTests
{
    /// <summary>The kept text and the flags of one case, as the systematic review (a paper) or the multivocal review (a page) makes them.</summary>
    public static (CleanedText Cleaned, List<string> Flags) Keep(AttackCase c)
    {
        var cleaned = c.IsPage ? PageText.Read(c.Text) : HiddenText.RemoveInvisible(c.Text);
        var flags = HiddenText.Flags(cleaned.Text, cleaned.Hidden)?.Select(f => f.Pattern).ToList() ?? new();
        return (cleaned, flags);
    }

    /// <summary>A prompt or a wrapped text has no invisible character; joiners between two non-ASCII characters are kept on purpose.</summary>
    public static bool HasNoInvisibleCharacters(string text) => HiddenText.RemoveInvisible(text).Text == text;

    [Theory]
    [MemberData(nameof(AttackSet.Ids), MemberType = typeof(AttackSet))]
    public void EachCaseRaisesItsFlagsAndLosesItsHiddenText(string id)
    {
        var c = AttackSet.Case(id);
        var (cleaned, flags) = Keep(c);

        Assert.Equal(c.Flags, flags);
        Assert.Equal(c.Invisible, cleaned.InvisibleCharacters);
        Assert.Equal(c.HiddenElements, cleaned.HiddenElements);
        Assert.True(HasNoInvisibleCharacters(cleaned.Text));
        Assert.All(c.NotToModel.Where(s => !s.Contains("UNTRUSTED", StringComparison.Ordinal)), s => Assert.DoesNotContain(s, cleaned.Text));
    }

    [Theory]
    [MemberData(nameof(AttackSet.Ids), MemberType = typeof(AttackSet))]
    public void TheWrappedTextHasOneOpeningAndOneClosingMarkerAndNothingHidden(string id)
    {
        var c = AttackSet.Case(id);
        // A paper's raw text as well as its kept one: Wrap must hold even if a caller forgot to clean the text first.
        foreach (string text in c.IsPage ? new[] { Keep(c).Cleaned.Text } : new[] { c.Text, Keep(c).Cleaned.Text })
        {
            string wrapped = PromptSafety.Wrap(text, "attack set");

            Assert.StartsWith(PromptSafety.OpenTag + " (attack set)\n", wrapped);
            Assert.EndsWith("\n" + PromptSafety.CloseTag, wrapped);
            Assert.Equal(1, wrapped.Split(PromptSafety.OpenTag).Length - 1);
            Assert.Equal(1, wrapped.Split(PromptSafety.CloseTag).Length - 1);
            Assert.True(HasNoInvisibleCharacters(wrapped));
            Assert.All(c.NotToModel, s => Assert.DoesNotContain(s, wrapped));
        }
    }

    [Theory]
    [MemberData(nameof(AttackSet.Ids), MemberType = typeof(AttackSet))]
    public void ScreeningSeesTheSameFlagsInAPapersAbstract(string id)
    {
        var c = AttackSet.Case(id);
        if (c.IsPage) return; // a page is screened on its search summary, not its HTML

        Assert.Equal(c.Flags, PromptSafety.Scan(c.Title, c.Text));
    }

    [Fact]
    public void TheCleanControlsAreKeptExactlyAsTheyAre()
    {
        var paper = AttackSet.Case("clean-control");
        Assert.Equal(paper.Text, Keep(paper).Cleaned.Text);
        Assert.Contains("‌", paper.Text);
        Assert.Contains("‍", paper.Text);

        var page = AttackSet.Case("page-clean-control");
        Assert.Equal(page.Kept, Keep(page).Cleaned.Text);
    }

    /// <summary>
    /// The known gaps are pinned down as they are: the hidden text is kept and reaches the model inside the markers, and
    /// the phrase scan flags it. When a gap is closed this test fails; move the case's expectation in the manifest then.
    /// </summary>
    [Fact]
    public void TheKnownGapsAreKeptButFlagged()
    {
        var gaps = AttackSet.All.Where(c => c.Gap != null).ToList();
        Assert.Equal(new[] { "page-class-hidden", "page-white-on-page" }, gaps.Select(c => c.Id));
        Assert.All(gaps, c =>
        {
            var (cleaned, flags) = Keep(c);
            Assert.Contains(c.ReachesModel!, cleaned.Text);
            Assert.NotEmpty(flags);
            Assert.DoesNotContain(HiddenText.FlagPattern, flags);
        });
    }

    [Fact]
    public void TheSetCoversEveryPatternAndBothKindsOfSource()
    {
        var patterns = AttackSet.All.SelectMany(c => c.Flags).ToHashSet();
        foreach (string pattern in new[] { "ignore-instructions", "addresses-the-model", "mentions-system-prompt", "demands-inclusion",
                     "claims-criteria-met", "chat-markup", "output-format-command", "reveal-instructions", "role-change" })
            Assert.True(PromptSafety.Find(string.Join("\n", AttackSet.All.Select(c => Keep(c).Cleaned.Hidden + "\n" + Keep(c).Cleaned.Text)))!
                .Any(f => f.Pattern == pattern), pattern);
        Assert.Contains(HiddenText.FlagPattern, patterns);
        Assert.Contains(AttackSet.All, c => c.IsPage);
        Assert.Contains(AttackSet.All, c => !c.IsPage);
        Assert.Equal(AttackSet.All.Count, AttackSet.All.Select(c => c.Title).Distinct().Count());
    }
}
